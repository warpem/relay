"""CPU-only regression checks for the development scheduler, without launching Relay."""
import importlib.util
import pathlib
import struct
import tempfile
import types
import unittest

script = pathlib.Path(__file__).resolve().parents[1] / 'warp-live-dev.py'
spec = importlib.util.spec_from_file_location('warp_live_dev', script)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class LauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.state = pathlib.Path(self.temporary.name).resolve()
        for name in ('input', 'logs', 'bin'):
            (self.state / name).mkdir()
        self.supervisor = launcher.Supervisor(self.state, types.SimpleNamespace(count=4, initial=2, interval=.1, size=64, frames=2))

    def tearDown(self):
        self.supervisor.close()
        self.temporary.cleanup()

    def test_cancel_only_owned_process_and_unknown_receipts(self):
        script = self.state / 'worker.sh'
        script.write_text('sleep 30\n')
        receipt = self.supervisor.dispatch(dict(operation='submit', script=str(script))).split('=')[1]
        self.assertEqual('RUNNING', self.supervisor.status(receipt))
        self.assertEqual('CANCELED', self.supervisor.dispatch(dict(operation='cancel', receipt=receipt)))
        self.assertIsNotNone(self.supervisor.jobs[receipt]['process'].poll())
        self.assertEqual('CANCELED', self.supervisor.status('prior-launch-receipt'))
        self.assertEqual('CANCELED', self.supervisor.dispatch(dict(operation='cancel', receipt='prior-launch-receipt')))

    def test_submission_requires_script_in_isolated_state(self):
        with self.assertRaises(ValueError):
            self.supervisor.dispatch(dict(operation='submit', script='/bin/sh'))
        link = self.state / 'escape.sh'
        link.symlink_to('/bin/sh')
        with self.assertRaises(ValueError):
            self.supervisor.dispatch(dict(operation='submit', script=str(link)))

    def test_process_exit_status_and_list(self):
        script = self.state / 'failure.sh'
        script.write_text('exit 7\n')
        receipt = self.supervisor.dispatch(dict(operation='submit', script=str(script))).split('=')[1]
        self.supervisor.jobs[receipt]['process'].wait(timeout=5)
        self.assertEqual('FAILED', self.supervisor.status(receipt))
        self.assertIn(receipt + ' FAILED', self.supervisor.dispatch(dict(operation='list')))

    def test_stop_requests_owned_host_shutdown(self):
        self.supervisor.dispatch(dict(operation='pause'))
        self.assertFalse(self.supervisor.stop.is_set())
        self.assertEqual('Development host stopping', self.supervisor.dispatch(dict(operation='stop')))
        self.assertTrue(self.supervisor.stop.is_set())

    def test_pause_injection_and_valid_mrc(self):
        self.supervisor.dispatch(dict(operation='pause'))
        self.assertTrue(self.supervisor.dispatch(dict(operation='status'))['sourcePaused'])
        path = pathlib.Path(self.supervisor.dispatch(dict(operation='inject', condition='badctf')))
        self.assertEqual('mock_000001_badctf.mrc', path.name)
        data = path.read_bytes()
        self.assertEqual((64, 64, 2, 2), struct.unpack_from('<4i', data))
        self.assertEqual(b'MAP ', data[208:212])
        self.assertEqual(1024 + 64 * 64 * 2 * 4, len(data))
        self.assertFalse(list(path.parent.glob('*.arriving')))
        self.supervisor.dispatch(dict(operation='resume'))
        self.assertFalse(self.supervisor.paused)
        self.assertEqual('mock_000002_normal.mrc', self.supervisor.inject('normal').name)


if __name__ == '__main__':
    unittest.main()
