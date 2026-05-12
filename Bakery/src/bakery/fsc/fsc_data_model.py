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

    @classmethod
    def from_relion_postprocess_star(cls, path: Path):
        raise NotImplementedError()

    @classmethod
    def from_relion_refine3d_model_star(cls, path: Path):
        star = starfile.read(path)
        df = star['model_class_1']
        data = {
            'resolution': df['rlnAngstromResolution'].to_numpy(),
            'fsc': df['rlnGoldStandardFsc'].to_numpy(),
        }
        return cls(**data)
