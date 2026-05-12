import os
from pathlib import Path

import matplotlib.axes
import numpy as np
from lxml import etree
from lxml.etree import ElementTree
from matplotlib.patches import Rectangle
from pydantic import BaseModel, ConfigDict
from scipy.interpolate import CubicSpline


class CTFParameters(BaseModel):
    pixel_size: float
    defocus: float
    amplitude: float
    cs: float
    voltage: float
    phase_shift: float

    @classmethod
    def from_warp_xml_root(cls, root: ElementTree):
        pixel_size = float(root.find("./CTF/Param[@Name='PixelSize']").attrib['Value'])
        defocus = float(root.find("./CTF/Param[@Name='Defocus']").attrib['Value'])
        amplitude = float(root.find("./CTF/Param[@Name='Amplitude']").attrib['Value'])
        cs = float(root.find("./CTF/Param[@Name='Cs']").attrib['Value'])
        voltage = float(root.find("./CTF/Param[@Name='Voltage']").attrib['Value'])
        phase_shift = float(root.find("./CTF/Param[@Name='PhaseShift']").attrib['Value'])
        instance = cls(
            pixel_size=pixel_size,
            defocus=defocus,
            amplitude=amplitude,
            cs=cs,
            voltage=voltage,
            phase_shift=phase_shift
        )
        return instance

    @classmethod
    def from_warp_xml(cls, file: os.PathLike):
        tree = etree.parse(file)
        root = tree.getroot()
        return cls.from_warp_xml_root(root=root)


class CTFFitInfo(BaseModel):
    model_config = ConfigDict(
        arbitrary_types_allowed=True
    )

    power_spectrum_1d: np.ndarray
    simulated_ctf_1d: np.ndarray
    experimental_scale: np.ndarray
    minimum_fitting_frequency: float  # fftfreq
    maximum_fitting_frequency: float  # fftfreq
    ctf_parameters: CTFParameters

    @classmethod
    def from_warp_xml_root(cls, root: ElementTree):
        power_spectrum_1d = cls.get_warp_ps1d(root)
        experimental_scale = cls.get_warp_ps1d_scale(root, n_elements=len(power_spectrum_1d))
        ctf_parameters = CTFParameters.from_warp_xml_root(root)
        simulated_ctf_1d = get_ctf_1d(
            num_elements=len(power_spectrum_1d),
            **ctf_parameters.model_dump()
        )
        minimum_fitting_frequency = 0.5 * float(
            root.find("./OptionsCTF/Param[@Name='RangeMin']").attrib['Value']
        )
        maximum_fitting_frequency = 0.5 * float(
            root.find("./OptionsCTF/Param[@Name='RangeMax']").attrib['Value']
        )
        instance = cls(
            power_spectrum_1d=power_spectrum_1d,
            simulated_ctf_1d=simulated_ctf_1d,
            experimental_scale=experimental_scale,
            minimum_fitting_frequency=minimum_fitting_frequency,
            maximum_fitting_frequency=maximum_fitting_frequency,
            ctf_parameters=ctf_parameters
        )
        return instance

    @classmethod
    def from_warp_xml(cls, file: os.PathLike):
        tree = etree.parse(file)
        root = tree.getroot()
        return cls.from_warp_xml_root(root=root)

    @classmethod
    def get_warp_ps1d(cls, root: ElementTree) -> np.ndarray:
        values = root.find("PS1D").text.split(';')
        return np.asarray([float(v.split('|')[1]) for v in values])

    @classmethod
    def get_warp_ps1d_scale(cls, root: ElementTree, n_elements: int) -> np.ndarray:
        values = root.find("SimulatedScale").text.split(';')

        # x is fraction of nyquist, y values are fit to RAPS at CTF zero crossings
        x = np.asarray([float(v.split('|')[0]) for v in values])
        y = np.asarray([float(v.split('|')[1]) for v in values])

        # let's get interpolated y values across whole spectrum
        scale_interpolator = CubicSpline(x=x, y=y, extrapolate=True)
        experimental_scale = scale_interpolator(np.linspace(0, 0.5, num=n_elements))
        return experimental_scale


def get_ctf_1d(
    num_elements,
    pixel_size,
    defocus,
    amplitude,
    cs,
    voltage,
    phase_shift=0,
):
    """
    Calculate 1D CTF for a given number of elements.

    Parameters:
    - num_elements (int): Number of elements in the 1D array.
    - pixel_size (float): Pixel size in Angstroms.
    - defocus (float): Defocus in micrometers.
    - amplitude (float): Amplitude contrast.
    - scale (float): Scale factor for the CTF.
    - phase_shift (float): Phase shift in pi radians.
    - cs (float): Spherical aberration in mm.
    - voltage (float): Acceleration voltage in kV.

    Returns:
    - np.ndarray: 1D array of CTF values.
    """
    # Constants
    defocus = -defocus * 1e4  # Convert to Angstroms
    phase_shift *= np.pi  # Convert phase shift to radians

    # Compute electron wavelength (in Angstroms)
    voltage = voltage * 1e3  # Convert to volts
    wavelength = 12.2643247 / np.sqrt(voltage * (1 + voltage * 0.978466e-6))

    # Precompute constants
    k1 = np.pi * wavelength
    k2 = np.pi * 0.5 * cs * 1e7 * wavelength ** 3
    k3 = np.sqrt(1 - amplitude ** 2)

    # Nyquist frequency
    nyquist_freq = 0.5 / (pixel_size * num_elements)

    # Compute spatial frequencies
    freqs = np.arange(num_elements) * nyquist_freq
    r2 = freqs ** 2
    r4 = r2 ** 2

    # Compute the CTF
    argument = k1 * defocus * r2 + k2 * r4 - phase_shift
    ctf = amplitude * np.cos(argument) - k3 * np.sin(argument)

    return ctf


def get_ctf_zero_crossings(
    pixel_size,
    defocus,
    amplitude,
    cs,
    voltage,
    bfactor=0,
    scale=1,
    phase_shift=0,
    amplitude_squared=False,
) -> np.ndarray:
    """"""
    ctf_1d = get_ctf_1d(
        num_elements=4096,
        pixel_size=pixel_size,
        defocus=defocus,
        amplitude=amplitude,
        cs=cs,
        voltage=voltage,
        phase_shift=phase_shift,
    )

    # find zero crossings
    # we do this by seeing where the sign of CTF changes
    ctf_sign = np.sign(ctf_1d)
    idx_sign_change = np.where(ctf_sign[:-1] != ctf_sign[1:])[0]
    zero_crossings = 0.5 * (idx_sign_change / (len(ctf_1d) - 1))  # cycles per pixel
    return zero_crossings


def get_ctf_peak_positions(
    pixel_size,
    defocus,
    amplitude,
    cs,
    voltage,
    phase_shift=0,
) -> np.ndarray:
    """"""
    ctf_1d = get_ctf_1d(
        num_elements=4096,
        pixel_size=pixel_size,
        defocus=defocus,
        amplitude=amplitude,
        cs=cs,
        voltage=voltage,
        phase_shift=phase_shift,
    )

    # find peak positions
    # we do this by seeing where the sign of the derivative of the CTF changes
    ctf_derivative = np.diff(ctf_1d)
    ctf_derivative_sign = np.sign(ctf_derivative)
    idx_sign_change = np.where(ctf_derivative_sign[:-1] != ctf_derivative_sign[1:])[0]
    peak_positions = 0.5 * (idx_sign_change / (len(ctf_1d) - 1))  # cycles per pixel
    return peak_positions


def estimate_ctf_fit_quality(
    ctf_parameters: CTFParameters,
    experimental_power_spectrum_1d: np.ndarray,
    experimental_power_spectrum_scale_1d: np.ndarray,
    minimum_fitting_frequency: float,  # units are normalized freqencies (cycles / px)
    minimum_samples: int = 16,
) -> np.ndarray:
    # we want to calculate quality by comparing simulated CTF to experimental PS
    # in a window size which covers one period of the CTF
    n = len(experimental_power_spectrum_1d)
    simulated_ctf_1d = get_ctf_1d(num_elements=n, **ctf_parameters.model_dump())

    # following values are in units of cycles/px
    zero_crossings = get_ctf_zero_crossings(**ctf_parameters.model_dump())
    peak_positions = get_ctf_peak_positions(**ctf_parameters.model_dump())
    peak_widths = np.diff(np.concatenate([[0], zero_crossings]))
    peak_wavelengths = 2 * peak_widths

    # calculate window size
    window_sizes = np.zeros(n)
    for i in range(n):
        dft_sample_frequency = 0.5 * (i / (n - 1))
        closest_peak_index = np.argmin(np.abs(peak_positions - dft_sample_frequency))
        wavelength_index = min(closest_peak_index, len(peak_wavelengths) - 1)
        window_sizes[i] = (peak_wavelengths[wavelength_index] / 0.5) * (n - 1)

    # initialise quality array
    quality = np.empty(n)
    quality.fill(np.nan)

    #
    simulated_ctf_1d = np.abs(simulated_ctf_1d) * experimental_power_spectrum_scale_1d
    min_n = int((minimum_fitting_frequency / 0.5) * (n - 1))
    max_n = n - minimum_samples
    for i in range(min_n, max_n):
        # calculate window start/end
        dw = int(window_sizes[i] // 2) if window_sizes[i] >= minimum_samples else minimum_samples // 2
        window_start = max(0, i - dw)
        window_end = min(n - 1, i + dw)

        # extract windows for comparison
        simulated_window = np.copy(simulated_ctf_1d[window_start:window_end])
        experimental_window = np.copy(experimental_power_spectrum_1d[window_start:window_end])

        # calculate normalised correlation in window
        simulated_window -= np.mean(simulated_window)
        simulated_window /= np.std(simulated_window)
        experimental_window -= np.mean(experimental_window)
        experimental_window /= np.std(experimental_window)
        quality[i] = np.dot(simulated_window, experimental_window) / (window_end - window_start)
    return quality


def draw_ctf_fit_quality_panel(
    ax: matplotlib.axes.Axes,
    item_xml_file: Path
):
    info = CTFFitInfo.from_warp_xml(item_xml_file)
    quality = estimate_ctf_fit_quality(
        ctf_parameters=info.ctf_parameters,
        experimental_power_spectrum_1d=info.power_spectrum_1d,
        experimental_power_spectrum_scale_1d=info.experimental_scale,
        minimum_fitting_frequency=info.minimum_fitting_frequency
    )
    linewidth = 0.4

    # plot simulated CTF and experimental curve
    ctf_1d = np.abs(info.simulated_ctf_1d) * info.experimental_scale
    ax.plot(ctf_1d, color='#FF1493', linewidth=linewidth)
    ax.plot(info.power_spectrum_1d, color='#00BFFF', linewidth=linewidth)

    # plot quality metric on a new y-axis, sharing x-axis
    ax2 = ax.twinx()
    ax2.plot(quality, color='#69696969', linewidth=linewidth)
    ax2.axis('off')

    # set y-axis max to max experimental scale found in fitting range
    fit_idx_min = int(info.minimum_fitting_frequency / 0.5 * (len(ctf_1d) - 1))
    fit_idx_max = int(info.maximum_fitting_frequency / 0.5 * (len(ctf_1d) - 1))
    scale_in_fitting_range = info.experimental_scale[fit_idx_min:fit_idx_max]
    scale_min, scale_max = np.min(scale_in_fitting_range), np.max(scale_in_fitting_range)
    scale_range = scale_max - scale_min
    ax.set_ylim(bottom=0 - 0.05 * scale_range, top=scale_max + 0.05 * scale_range)

    # add box showing frequency range used for fit
    box = Rectangle(xy=(fit_idx_min, -1.25), width=fit_idx_max - fit_idx_min, height=2.5, facecolor='#69696935')
    ax2.add_patch(box)
    ax2.set_ylim(bottom=0, top=1.05)

    # remove spines
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)

    # remove ticks and labels
    ax.axis('off')

    return
