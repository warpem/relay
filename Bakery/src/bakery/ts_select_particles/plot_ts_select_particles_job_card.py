import numpy as np
import matplotlib.axes
from pathlib import Path
import mrcfile
import matplotlib.pyplot as plt
from ..image_utils import process_image_for_visualization


def parse_star_file_coordinates(star_file: Path) -> np.ndarray:
    """
    Parse RELION STAR file to extract particle coordinates.
    
    Returns:
        Array of coordinates with shape (N, 3) where columns are [X, Y, Z]
    """
    coordinates = []
    
    with open(star_file, 'r') as f:
        lines = f.readlines()
    
    # Find the data section
    in_data_section = False
    header_map = {}
    
    for line in lines:
        line = line.strip()
        
        if line.startswith('data_'):
            in_data_section = False
            continue
            
        if line.startswith('loop_'):
            in_data_section = True
            continue
            
        if in_data_section and line.startswith('_'):
            # Parse header to find coordinate columns
            parts = line.split()
            column_name = parts[0]
            column_index = len(header_map)
            
            if 'rlnCoordinateX' in column_name:
                header_map['x'] = column_index
            elif 'rlnCoordinateY' in column_name:
                header_map['y'] = column_index
            elif 'rlnCoordinateZ' in column_name:
                header_map['z'] = column_index
                
        elif in_data_section and not line.startswith('_') and line:
            # Parse data line
            parts = line.split()
            if len(parts) > max(header_map.values()) if header_map else False:
                x = float(parts[header_map.get('x', 0)])
                y = float(parts[header_map.get('y', 1)])
                z = float(parts[header_map.get('z', 2)])
                coordinates.append([x, y, z])
    
    result = np.array(coordinates) if coordinates else np.empty((0, 3))
    print(f"Loaded {len(result)} particles from {star_file}")
    return result


def plot_ts_select_particles_job_card(
    ax1: matplotlib.axes.Axes,
    ax2: matplotlib.axes.Axes,
    mrc_file_1: Path,
    star_file_1: Path,
    mrc_file_2: Path,
    star_file_2: Path,
    particle_diameter_angstroms: float
):
    """Plot particle selection job card with two tomogram slices and particle positions."""
    
    def process_tomogram_and_particles(mrc_file: Path, star_file: Path, ax: matplotlib.axes.Axes):
        """Process a single tomogram and plot with particle positions."""
        
        # Load tomogram and get voxel size
        with mrcfile.open(mrc_file) as mrc:
            tomogram = mrc.data.astype(np.float32)
            voxel_size_angstroms = float(mrc.voxel_size.x)  # Angstroms per voxel
        
        # Calculate particle diameter in voxels
        particle_diameter_voxels = particle_diameter_angstroms / voxel_size_angstroms
        
        # Calculate thickness based on template diameter (following C# logic lines 768-771)
        z_thickness = max(1, int(particle_diameter_angstroms / voxel_size_angstroms))
        z_center = tomogram.shape[0] // 2
        z_min = max(0, int(z_center - z_thickness // 2))
        z_max = min(tomogram.shape[0], int(z_center + z_thickness // 2))
        
        # Average central slices
        tomogram_slice = np.mean(tomogram[z_min:z_max], axis=0)
        
        # Parse particle coordinates from STAR file
        coordinates = parse_star_file_coordinates(star_file)
        
        # Normalize tomogram like in C# code (following lines 784-791)
        # Calculate mean and std from central quarter for normalization
        h, w = tomogram_slice.shape
        central_quarter = tomogram_slice[h//4:3*h//4, w//4:3*w//4]
        mean_val = np.mean(central_quarter)
        std_val = np.std(central_quarter)
        
        # Normalize like C# code: (v - SliceMin) / (SliceMax - SliceMin) * 255
        slice_min = mean_val - std_val * 3
        slice_max = mean_val + std_val * 3
        tomogram_normalized = (tomogram_slice - slice_min) / (slice_max - slice_min)
        tomogram_normalized = np.clip(tomogram_normalized, 0, 1)
        
        # Now downsample the normalized image
        tomogram_processed = process_image_for_visualization(tomogram_normalized, target_long_side=256)
        
        # Calculate scale factor for coordinate conversion
        scale_factor = min(256 / h, 256 / w) if max(h, w) > 256 else 1.0
        
        # Display tomogram
        ax.imshow(tomogram_processed, cmap="gray", origin="lower", vmin=0, vmax=1, aspect='equal', interpolation='sinc')
        
        # Plot particle positions as yellow circles
        if len(coordinates) > 0:
            # Convert coordinates from normalized [0,1] to voxel coordinates
            coords_voxels = coordinates.copy()
            coords_voxels[:, 0] *= w  # X coordinates
            coords_voxels[:, 1] *= h  # Y coordinates
            coords_voxels[:, 2] *= tomogram.shape[0]  # Z coordinates
            
            # Filter particles that are in the central slice range
            z_coords = coords_voxels[:, 2]
            mask = (z_coords >= z_min) & (z_coords <= z_max)
            visible_coords = coords_voxels[mask]
            
            if len(visible_coords) > 0:
                # Scale coordinates to match the processed image
                x_coords = visible_coords[:, 0] * scale_factor
                y_coords = visible_coords[:, 1] * scale_factor
                
                # Scale particle radius
                radius_pixels = (particle_diameter_voxels / 2) * scale_factor
                
                # Draw circles (following C# logic from lines 834-838)
                for x, y in zip(x_coords, y_coords):
                    circle = plt.Circle((x, y), radius_pixels, 
                                      color='yellow', fill=False, linewidth=0.42)
                    ax.add_patch(circle)
        
        ax.axis('off')
    
    # Process both tomograms
    process_tomogram_and_particles(mrc_file_1, star_file_1, ax1)
    process_tomogram_and_particles(mrc_file_2, star_file_2, ax2)