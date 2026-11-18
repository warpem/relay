"""Execute a finite figure batch in one interpreter, sharing plotting caches."""
import json
import os
import shlex
from pathlib import Path
import typer
import matplotlib.pyplot as plt
from ._cli import cli


@cli.command(no_args_is_help=True)
def batch(manifest: Path = typer.Option(...)):
    command = typer.main.get_command(cli)
    for item in json.loads(manifest.read_text()):
        arguments = item['ArgumentList'] or shlex.split(item['Arguments'])
        if not arguments or arguments[0] == 'batch':
            raise ValueError('Invalid nested Bakery batch')
        previous = Path.cwd()
        try:
            if item['WorkingDirectory']:
                os.chdir(item['WorkingDirectory'])
            command.main(args=arguments, standalone_mode=False)
        finally:
            # Legacy commands relied on interpreter exit to release figures.
            plt.close('all')
            os.chdir(previous)
