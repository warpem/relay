#!/usr/bin/env python3
"""Run actual Relay + disk queue + WarpWorker2 --mock in an isolated local workspace."""
from __future__ import annotations
import argparse
import array
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import shlex
import shutil
import signal
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time
import urllib.request
import uuid

MARKER = '.warp-live-development'
CONDITIONS = ('normal', 'highmotion', 'badctf', 'thickice', 'fewparticles', 'fail')


def save_json(path, data):
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(data, indent=2) + '\n')
    temporary.chmod(0o600)
    temporary.replace(path)


def socket_path(state):
    directory = Path('/tmp') / ('relay-warp-dev-' + str(os.getuid()))
    directory.mkdir(mode=0o700, exist_ok=True)
    if directory.stat().st_uid != os.getuid() or directory.is_symlink():
        raise RuntimeError('Development socket directory must be owned by this user')
    directory.chmod(0o700)
    return directory / (hashlib.sha256(str(state).encode()).hexdigest()[:20] + '.sock')


def rpc(state, operation, **values):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(20)
        client.connect(str(socket_path(state)))
        client.sendall((json.dumps(dict(operation=operation, **values)) + '\n').encode())
        reply = json.loads(client.makefile('r').readline())
    if 'error' in reply:
        raise RuntimeError(reply['error'])
    return reply['result']


def write_movie(path, index, size=256, frames=4):
    """Valid little-endian float MRC; input only, never fabricated processing results."""
    header = bytearray(1024)
    struct.pack_into('<4i', header, 0, size, size, frames, 2)
    struct.pack_into('<3i', header, 28, size, size, frames)
    struct.pack_into('<3f', header, 40, size * 2., size * 2., frames * 2.)
    struct.pack_into('<3f', header, 52, 90., 90., 90.)
    struct.pack_into('<3i', header, 64, 1, 2, 3)
    struct.pack_into('<3f', header, 76, -2., 2., 0.)
    header[208:212] = b'MAP '
    header[212:216] = b'DD\0\0'
    struct.pack_into('<i', header, 220, 1)
    label = b'MOCK development input; synthetic data, no scientific interpretation'
    header[224:224 + len(label)] = label
    plane = array.array('f', (math.sin(x * .093 + index) * math.cos(y * .071) + .3 * math.sin(x * .31 + y * .17) for y in range(size) for x in range(size)))
    if sys.byteorder != 'little':
        plane.byteswap()
    temporary = path.with_suffix('.arriving')
    with temporary.open('wb') as stream:
        stream.write(header)
        for _ in range(frames):
            plane.tofile(stream)
    temporary.replace(path)


def terminate(process):
    # Handles belong to this live supervisor. No PID is loaded from persistent state.
    if process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)
    except ProcessLookupError:
        pass


class Supervisor:
    def __init__(self, state, args):
        self.state, self.args = state, args
        self.jobs = {}
        self.lock = threading.RLock()
        self.source_lock = threading.Lock()
        self.stop = threading.Event()
        self.paused = False
        self.env = dict(os.environ, PATH=str(state / 'bin') + os.pathsep + os.environ.get('PATH', ''))

    def status(self, receipt):
        record = self.jobs.get(receipt)
        if record is None:
            return 'CANCELED'  # Receipt from an earlier launcher has no owned process.
        if record['canceled']:
            return 'CANCELED'
        code = record['process'].poll()
        return 'RUNNING' if code is None else ('SUCCEEDED' if code == 0 else 'FAILED')

    def dispatch(self, request):
        operation = request['operation']
        with self.lock:
            if operation == 'submit':
                script = Path(request['script']).resolve()
                if not script.is_relative_to(self.state) or not script.is_file():
                    raise ValueError('Only scripts inside this development state may be submitted')
                receipt = uuid.uuid4().hex
                log = (self.state / 'logs' / ('scheduler-' + receipt + '.log')).open('ab')
                try:
                    process = subprocess.Popen(['/bin/bash', str(script)], cwd=script.parent, env=self.env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                finally:
                    log.close()
                self.jobs[receipt] = dict(process=process, canceled=False)
                return 'receipt=' + receipt
            if operation == 'job-status':
                return self.status(request['receipt'])
            if operation == 'cancel':
                record = self.jobs.get(request['receipt'])
                if record:
                    record['canceled'] = True
                    terminate(record['process'])
                return 'CANCELED'
            if operation == 'list':
                return '\n'.join(receipt + ' ' + self.status(receipt) for receipt in self.jobs)
            if operation in ('pause', 'resume'):
                self.paused = operation == 'pause'
                return 'Source paused' if self.paused else 'Source resumed'
            if operation == 'stop':
                self.stop.set()
                return 'Development host stopping'
            if operation == 'status':
                return dict(sourcePaused=self.paused, inputs=len(list((self.state / 'input').glob('*.mrc'))), inputTarget=self.args.count, jobs={key: self.status(key) for key in self.jobs})
        if operation == 'inject':
            return str(self.inject(request.get('condition', 'normal')))
        raise ValueError('Unknown development operation: ' + operation)

    def inject(self, condition):
        if condition not in CONDITIONS:
            raise ValueError('Unknown mock condition')
        with self.source_lock:
            existing = list((self.state / 'input').glob('mock_*.mrc'))
            index = max((int(p.name.split('_')[1]) for p in existing), default=0) + 1
            path = self.state / 'input' / f'mock_{index:06d}_{condition}.mrc'
            write_movie(path, index, self.args.size, self.args.frames)
            return path

    def stream(self):
        while not self.stop.is_set():
            count = len(list((self.state / 'input').glob('*.mrc')))
            if not self.paused and count < self.args.count:
                # Occasional image-quality episodes, all explicitly mock data.
                condition = {9: 'highmotion', 13: 'badctf', 17: 'thickice', 21: 'fewparticles'}.get(count, 'normal')
                self.inject(condition)
                delay = .05 if count + 1 < self.args.initial else self.args.interval
            else:
                delay = .25
            self.stop.wait(delay)

    def close(self):
        self.stop.set()
        with self.lock:
            for record in self.jobs.values():
                terminate(record['process'])


class RpcHandler(socketserver.StreamRequestHandler):
    def handle(self):
        try:
            line = self.rfile.readline(65537)
            if len(line) > 65536:
                raise ValueError('Request too large')
            result = self.server.supervisor.dispatch(json.loads(line))
            reply = dict(result=result)
        except Exception as error:
            reply = dict(error=str(error))
        self.wfile.write((json.dumps(reply) + '\n').encode())


class RpcServer(socketserver.ThreadingUnixStreamServer):
    daemon_threads = True


def run(args, state):
    if any(character in str(state) for character in "'\n\r"):
        raise ValueError('Development state path cannot contain quotes or line breaks')
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    if not (state / MARKER).exists() and any(state.iterdir()):
        raise RuntimeError('Refusing nonempty unmarked directory; choose a fresh --state path')
    state.chmod(0o700)
    (state / MARKER).touch(mode=0o600)
    lock = (state / '.launcher.lock').open('w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise RuntimeError('A launcher already owns this state')
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', args.port))
    for directory in ('logs', 'bin', 'input', 'space', 'build'):
        (state / directory).mkdir(exist_ok=True)
    repo = Path(__file__).resolve().parents[1]
    dotnet = shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('.NET 10 SDK is required')
    if not args.no_build:
        for project in ('Relay/Relay.csproj', 'Warp/WarpWorker2/WarpWorker2.csproj'):
            subprocess.run([dotnet, 'build', str(repo / project), '-c', 'Debug', '--nologo', '-v', 'minimal'], cwd=repo, check=True)
    build = repo / 'bin' / 'debug'
    worker = repo / 'Warp' / 'bin' / 'WarpWorker2.dll'
    if not (build / 'Relay.dll').is_file() or not worker.is_file():
        raise RuntimeError('Build Relay and its Warp/WarpWorker2 first, or omit --no-build')
    script = str(Path(__file__).resolve())
    command = shlex.join([sys.executable, script, '--state', str(state)])
    metadata_path = state / 'dev-session.json'
    metadata = json.loads(metadata_path.read_text()) if metadata_path.exists() else dict(username='warp-dev', password=secrets.token_urlsafe(18))
    worker_args = ['--mock', '--mock-delay-ms', str(args.mock_delay_ms)]
    if args.mock_fail_command:
        worker_args += ['--mock-fail-command', args.mock_fail_command]
    if args.mock_fail_path:
        worker_args += ['--mock-fail-path', args.mock_fail_path]
    save_json(state / 'worker.json', dict(dotnet=dotnet, dll=str(worker), args=worker_args))
    wrapper = state / 'bin' / 'WarpWorker2'
    wrapper.write_text('#!/bin/bash\nexec ' + command + ' worker -- "$@"\n')
    wrapper.chmod(0o700)
    endpoint = socket_path(state)
    endpoint.unlink(missing_ok=True)  # Lock proves no live launcher owns this socket.
    supervisor = Supervisor(state, args)
    server = RpcServer(str(endpoint), RpcHandler)
    server.supervisor = supervisor
    endpoint.chmod(0o600)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    relay = None
    try:
        if not (state / 'bootstrap-result.json').exists():
            save_json(state / 'bootstrap.json', dict(state=str(state), schedulerCommand=command, username=metadata['username'], password=metadata['password']))
            seeder = repo / 'scripts' / 'warp-live-dev' / 'Seeder' / 'Seeder.csproj'
            subprocess.run([dotnet, 'build', str(seeder), '-o', str(state / 'build'), '-p:RelayBuildDir=' + str(build), '-p:BaseIntermediateOutputPath=' + str(state / 'build' / 'obj') + '/', '--nologo', '-v', 'minimal'], check=True)
            subprocess.run([dotnet, 'exec', '--runtimeconfig', str(build / 'Relay.runtimeconfig.json'), '--depsfile', str(build / 'Relay.deps.json'), str(state / 'build' / 'Seeder.dll'), str(build), str(state / 'bootstrap.json')], cwd=state, env=supervisor.env, check=True)
        metadata.update(json.loads((state / 'bootstrap-result.json').read_text()))
        metadata.update(url=f'http://127.0.0.1:{args.port}', state=str(state), socketPath=str(endpoint))
        save_json(metadata_path, metadata)
        env = dict(supervisor.env, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=metadata['url'], ASPNETCORE_CONTENTROOT=str(repo / 'Relay'))
        with (state / 'logs' / 'relay.log').open('ab') as log:
            relay = subprocess.Popen([dotnet, str(build / 'Relay.dll')], cwd=state, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        threading.Thread(target=supervisor.stream, daemon=True).start()
        for _ in range(180):
            if relay.poll() is not None:
                raise RuntimeError('Relay exited; see ' + str(state / 'logs' / 'relay.log'))
            try:
                urllib.request.urlopen(metadata['url'], timeout=1).close()
                break
            except Exception:
                time.sleep(.5)
        else:
            raise RuntimeError('Relay did not become ready; see logs/relay.log')
        print('\nMOCK Warp Live: ' + metadata['url'] + metadata['route'], flush=True)
        print('Login: ' + metadata['username'] + ' / ' + metadata['password'], flush=True)
        print('Click Start in Warp Live. Ctrl+C stops this host and its owned workers.', flush=True)
        print('Runtime metadata: ' + str(metadata_path), flush=True)
        while relay.poll() is None and not supervisor.stop.is_set():
            time.sleep(.5)
    finally:
        supervisor.stop.set()
        if relay:
            terminate(relay)
        supervisor.close()
        server.shutdown()
        server.server_close()
        endpoint.unlink(missing_ok=True)
        lock.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog='Example: python3 scripts/warp-live-dev.py --no-build; use pause/resume/status/stop in a second terminal. No real GPU work is submitted.')
    parser.add_argument('--state', type=Path, default=Path('/tmp') / ('relay-warp-live-dev-' + str(os.getuid())))
    parser.add_argument('--port', type=int, default=5241)
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--count', type=int, default=24, help='Total arriving inputs; persisted inputs count toward this target')
    parser.add_argument('--interval', type=float, default=2, help='Seconds between arrivals after initial burst')
    parser.add_argument('--initial', type=int, default=4)
    parser.add_argument('--size', type=int, default=256, help='Synthetic MRC width and height')
    parser.add_argument('--frames', type=int, default=4)
    parser.add_argument('--mock-delay-ms', type=int, default=250)
    parser.add_argument('--mock-fail-command')
    parser.add_argument('--mock-fail-path')
    parser.add_argument('action', nargs='?', default='run', choices=('run', 'status', 'pause', 'resume', 'stop', 'inject', 'submit', 'job-status', 'cancel', 'list', 'worker'))
    parser.add_argument('value', nargs='?')
    parser.add_argument('--condition', choices=CONDITIONS, default='normal')
    raw = sys.argv[1:]
    worker_args = []
    if '--' in raw:
        position = raw.index('--')
        raw, worker_args = raw[:position], raw[position + 1:]
    args = parser.parse_args(raw)
    state = args.state.expanduser().resolve()
    if args.action == 'run':
        def stop_signal(signum, frame):
            raise KeyboardInterrupt
        signal.signal(signal.SIGTERM, stop_signal)
    if args.action == 'worker':
        config = json.loads((state / 'worker.json').read_text())
        os.execv(config['dotnet'], [config['dotnet'], config['dll'], *config['args'], *worker_args])
    if args.action == 'run':
        if args.count < 0 or args.interval <= 0 or args.size < 64 or args.size > 2048 or args.frames < 1 or args.frames > 64:
            parser.error('Use count >= 0, interval > 0, size 64..2048, frames 1..64')
        run(args, state)
    else:
        values = dict(script=args.value) if args.action == 'submit' else dict(receipt=args.value)
        if args.action == 'inject':
            values = dict(condition=args.condition)
        result = rpc(state, args.action, **values)
        print(json.dumps(result, indent=2) if isinstance(result, dict) else result)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (RuntimeError, ValueError, OSError, subprocess.CalledProcessError) as error:
        print('warp-live-dev: ' + str(error), file=sys.stderr)
        sys.exit(1)
