"""Static visualization generation for RELAY"""

from importlib.metadata import PackageNotFoundError, version

try:
    __version__ = version("Bakery")
except PackageNotFoundError:
    __version__ = "uninstalled"
__author__ = "Alister Burt"
__email__ = "burt.alister@gene.com"

from .cli import cli
