import typer

# Create the 'bakery' CLI tool here, entry point is defined in pyproject.toml under
# [project.scripts]
cli = typer.Typer(
    name='bakery',
    no_args_is_help=True,
    add_completion=False,
    help="A CLI for generating visualizations of cryo-EM data for RELAY"
)
