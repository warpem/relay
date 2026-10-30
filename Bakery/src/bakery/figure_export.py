"""Static, self-contained figures with an adaptive display and fixed export palettes.

Only vector paint is themed. Embedded scientific image pixels are untouched.
Artists with a ``fixed-`` gid (e.g. labels on density images) retain their paint.
"""
from io import StringIO
from pathlib import Path
import os
import re
import tempfile
import xml.etree.ElementTree as ET

import matplotlib as mpl

SVG = 'http://www.w3.org/2000/svg'
ET.register_namespace('', SVG)
ET.register_namespace('xlink', 'http://www.w3.org/1999/xlink')

# Matplotlib's neutral paints and default series colors, not arbitrary data colors.
PAINTS = {
    '#ffffff': ('surface', '#ffffff', '#333333'),
    '#000000': ('ink', '#242424', '#eeeeee'),
    '#b0b0b0': ('grid', '#b0b0b0', '#767676'),
    '#7f7f7f': ('muted', '#666666', '#bcbcbc'),
    '#1f77b4': ('blue', '#1f77b4', '#70b8e8'),
    '#ff7f0e': ('orange', '#bc650b', '#ffae58'),
    '#2ca02c': ('green', '#278327', '#81cb84'),
    '#9467bd': ('purple', '#8059ab', '#c2a0e3'),
    '#ff0000': ('threshold', '#c43c39', '#f08080'),
}
_PAINT = re.compile(r'(\b(?:fill|stroke):\s*)(#[0-9a-fA-F]{6})(?=\s*(?:;|$))')


def _write_atomic(path: Path, content: str):
    """Never publish a partially written SVG to a live job viewer."""
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent,
                                     prefix=f'.{path.name}.', delete=False) as stream:
        temporary = Path(stream.name)
        try:
            stream.write(content)
        except BaseException:
            temporary.unlink(missing_ok=True)
            raise
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def themed_svg(source: str, theme: str = 'auto') -> str:
    """Resolve paint roles without touching raster data, geometry or fixed artists."""
    if theme not in ('auto', 'light', 'dark'):
        raise ValueError(f'Unknown figure theme: {theme}')
    root = ET.fromstring(source)

    def color(value):
        entry = PAINTS.get(value.lower())
        if entry is None:
            return value
        role, light, dark = entry
        return f'var(--figure-{role}, {light})' if theme == 'auto' else (dark if theme == 'dark' else light)

    def visit(node, fixed=False):
        fixed = fixed or node.get('id', '').startswith('fixed-')
        if not fixed:
            if 'style' in node.attrib:
                node.set('style', _PAINT.sub(lambda m: m[1] + color(m[2]), node.get('style')))
            for attribute in ('fill', 'stroke'):
                if attribute in node.attrib:
                    node.set(attribute, color(node.get(attribute)))
        for child in node:
            visit(child, fixed)

    # Matplotlib omits black text's fill because it is SVG's default. Supply an
    # inherited foreground as well as rewriting explicitly painted artists.
    root.set('fill', color('#000000'))
    visit(root)
    if theme == 'auto':
        style = ET.Element(f'{{{SVG}}}style', {'type': 'text/css'})
        light = ';'.join(f'--figure-{role}:{light}' for role, light, _ in PAINTS.values())
        dark = ';'.join(f'--figure-{role}:{dark}' for role, _, dark in PAINTS.values())
        style.text = ':root{' + light + '}\n@media (prefers-color-scheme:dark){:root{' + dark + '}}'
        root.insert(0, style)
    return ET.tostring(root, encoding='unicode', xml_declaration=True)


def save_figure(fig, output_file: Path, *, svg: bool = False, **kwargs):
    """Keep the requested output; optionally add auto/light/dark SVG siblings.

    SVGs embed images and use text rather than external fonts or glyph assets.
    Fixed exports have literal colors, for applications without CSS variables.
    """
    path = Path(output_file)
    if path.suffix.lower() != '.svg':
        fig.savefig(path, **kwargs)
    if svg or path.suffix.lower() == '.svg':
        buffer = StringIO()
        with mpl.rc_context({'svg.fonttype': 'none', 'svg.image_inline': True}):
            fig.savefig(buffer, format='svg', metadata={'Date': None}, **kwargs)
        source = buffer.getvalue()
        target = path.with_suffix('.svg')
        # Write the adaptive image last; its presence means downloads are ready.
        for theme in ('light', 'dark', 'auto'):
            destination = target if theme == 'auto' else target.with_suffix(f'.{theme}.svg')
            _write_atomic(destination, themed_svg(source, theme))
