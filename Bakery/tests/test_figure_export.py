from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib.pyplot as plt
import numpy as np

from bakery.figure_export import save_figure, themed_svg, SVG


def test_svg_exports_embed_identical_image_pixels_and_keep_labels_fixed(tmp_path):
    fig, ax = plt.subplots()
    ax.imshow(np.arange(100).reshape(10, 10), cmap='gray')
    ax.text(1, 1, 'XY', color='white', gid='fixed-plane-xy')
    ax.set_xlabel('Resolution (1 / Å)')
    ax.plot([0, 9], [0, 9], color='black')
    output = tmp_path / 'figure.png'
    save_figure(fig, output, svg=True)
    plt.close(fig)
    assert output.read_bytes().startswith(b'\x89PNG')
    images = []
    for name in ('figure.svg', 'figure.light.svg', 'figure.dark.svg'):
        text = (tmp_path / name).read_text()
        root = ET.fromstring(text)
        assert root.get('fill') == ({'figure.svg': 'var(--figure-ink, #242424)',
                                    'figure.light.svg': '#242424',
                                    'figure.dark.svg': '#eeeeee'}[name])
        image = root.find(f'.//{{{SVG}}}image')
        href = image.get('{http://www.w3.org/1999/xlink}href')
        assert href.startswith('data:image/png;base64,')
        images.append(href)
        label = root.find(f'.//{{{SVG}}}g[@id="fixed-plane-xy"]')
        assert '#ffffff' in ET.tostring(label, encoding='unicode')
        assert root.findall(f'.//{{{SVG}}}text')
        assert '<script' not in text
        if name == 'figure.svg':
            assert '@media (prefers-color-scheme:dark)' in text
            assert 'var(--figure-ink' in text
        else:
            assert 'var(' not in text
            assert '@media' not in text
    assert images[0] == images[1] == images[2]
    assert '#eeeeee' in (tmp_path / 'figure.dark.svg').read_text()
    assert '#242424' in (tmp_path / 'figure.light.svg').read_text()


def test_export_does_not_recolor_arbitrary_series_or_mutate_the_figure(tmp_path):
    fig, ax = plt.subplots()
    line, = ax.plot([0, 1], [0, 1], color='#123456')
    save_figure(fig, tmp_path / 'test.svg')
    assert line.get_color() == '#123456'
    assert '#123456' in (tmp_path / 'test.dark.svg').read_text()
    assert not (tmp_path / 'test.png').exists()
    plt.close(fig)


def test_invalid_theme_is_rejected():
    import pytest
    with pytest.raises(ValueError):
        themed_svg('<svg/>', 'night')
