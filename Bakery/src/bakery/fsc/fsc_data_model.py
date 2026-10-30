from dataclasses import dataclass
from pathlib import Path

import numpy as np
import starfile


@dataclass
class FscData:
    resolution: np.ndarray
    fsc: np.ndarray
    fsc_masked: np.ndarray | None = None
    fsc_unmasked: np.ndarray | None = None
    fsc_phase_randomized: np.ndarray | None = None
    estimated_resolution: float | None = None
    directional_best: float | None = None
    directional_worst: float | None = None
    cfar: float | None = None

    @classmethod
    def from_relion_postprocess_star(cls, path: Path):
        raise NotImplementedError()

    @classmethod
    def from_relion_refine3d_model_star(cls, path: Path):
        star = starfile.read(path, always_dict=True)
        return cls.from_model_tables(star)

    @classmethod
    def from_model_tables(cls, star):
        result = cls.from_model_frame(star['model_class_1'])
        classes = star.get('model_classes')
        if result.fsc_unmasked is not None and classes is not None and 'rlnEstimatedResolution' in classes:
            resolution = float(classes['rlnEstimatedResolution'].iloc[0])
            if np.isfinite(resolution) and resolution > 0:
                result.estimated_resolution = resolution
        if classes is not None:
            for field, column in [('directional_best', 'rlnDirectionalBestResolution'),
                                  ('directional_worst', 'rlnDirectionalWorstResolution'),
                                  ('cfar', 'rlnCylindricalFscAreaRatio')]:
                if column in classes and len(classes):
                    value = float(classes[column].iloc[0])
                    if np.isfinite(value):
                        setattr(result, field, value)
        return result

    @classmethod
    def from_model_frame(cls, df):
        if 'rlnGoldStandardFsc' not in df:
            unmasked = df['rlnFourierShellCorrelationUnmaskedMaps'].to_numpy()
            corrected = df.get('rlnFourierShellCorrelationCorrected')
            masked = df.get('rlnFourierShellCorrelationMaskedMaps')
            randomized = df.get('rlnCorrectedFourierShellCorrelationPhaseRandomizedMaskedMaps')
            return cls(
                resolution=df['rlnAngstromResolution'].to_numpy(),
                fsc=unmasked if corrected is None else corrected.to_numpy(),
                fsc_unmasked=unmasked,
                fsc_masked=None if masked is None else masked.to_numpy(),
                fsc_phase_randomized=None if randomized is None else randomized.to_numpy(),
            )
        data = {
            'resolution': df['rlnAngstromResolution'].to_numpy(),
            'fsc': df['rlnGoldStandardFsc'].to_numpy(),
        }
        return cls(**data)
