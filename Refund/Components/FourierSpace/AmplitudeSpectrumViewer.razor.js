/**
 * @fileoverview JavaScript module for the AmplitudeSpectrumViewer component.
 * Handles chart creation, interaction, and destruction using Chart.js.
 */

import * as annotationPlugin from 'https://cdn.jsdelivr.net/npm/chartjs-plugin-annotation/dist/chartjs-plugin-annotation.min.js';

// Module state
let dotNetRef;
let chart = null;
let binnedPixelSize = null;

// Colors for different data series in the chart
const COLORS = {
    experimental: 'rgb(0, 191, 255)',    // Cyan for experimental data
    simulated: 'rgb(255, 20, 147)',      // Pink for simulated/fitted data
    quality: 'rgb(211, 211, 211)',       // Light gray for quality metrics
    annotation: 'rgba(192, 192, 192, 0.1)' // Semi-transparent gray for annotations
};

/**
 * Initializes the module with a reference to the .NET component
 * and registers the annotation plugin for Chart.js.
 * 
 * @param {DotNetObjectReference} dotNetReference - Reference to the .NET component
 */
export function initialize(dotNetReference) {
    dotNetRef = dotNetReference;
    Chart.register(annotationPlugin);
}

/**
 * Sets up the Chart.js visualization with CTF fitting data.
 * 
 * Creates a multi-dataset line chart with three series:
 * 1. Experimental data from the power spectrum
 * 2. Fitted/simulated CTF model
 * 3. Quality metrics showing goodness of fit
 * 
 * Also highlights the fitting range with a box annotation.
 * 
 * @param {HTMLCanvasElement} canvas - The canvas element to render the chart on
 * @param {Object} config - Configuration object with data series and display parameters
 */
export function setupChart(canvas, config) {
    // Clean up any existing chart
    if (chart) {
        destroyChart();
    }

    // Store pixel size for Angstrom calculations
    binnedPixelSize = config.binnedPixelSize;
    const ctx = canvas.getContext('2d');

    // Prepare data for the chart with three datasets
    const chartData = {
        labels: Array.from({ length: config.experimentalValues.length }, (_, i) => i),
        datasets: [
            createDataset('Experimental', config.experimentalValues, 'yExperimental', COLORS.experimental),
            createDataset('Fitted', config.simulatedValues, 'ySimulated', COLORS.simulated),
            createDataset('Quality', config.qualityValues, 'yQuality', COLORS.quality)
        ]
    };

    // Create the chart with specific options for CTF visualization
    chart = new Chart(ctx, {
        type: 'line',
        data: chartData,
        options: {
            responsive: false,
            maintainAspectRatio: false,
            animation: { duration: 0 },             // Disable animations for performance
            interaction: {
                intersect: false,                   // Hover anywhere along x-axis
                mode: 'index'                       // Show all values at same x-position
            },
            elements: {
                point: { radius: 0 },               // Don't show individual points
                line: { tension: 0.4 }              // Slight curve for visual appeal
            },
            layout: {
                autoPadding: false,
                padding: 0
            },
            scales: {
                x: {
                    type: 'linear',
                    display: false,                 // Hide x-axis (shown in tooltip as Angstroms)
                    ticks: {
                        beginAtZero: true,
                        stepSize: 0.5
                    }
                },
                yExperimental: {
                    type: 'linear',
                    display: false,                 // Hide y-axis for cleaner look
                    position: 'left',
                    min: config.minNormalized,
                    max: config.maxNormalized,
                    ticks: { stepSize: 100 }
                },
                ySimulated: {
                    type: 'linear',
                    display: false,
                    position: 'left',
                    min: config.minNormalized,
                    max: config.maxNormalized,
                    ticks: { stepSize: 10 }
                },
                yQuality: {
                    type: 'linear',
                    display: false,
                    position: 'right',
                    min: 0,
                    max: 1                          // Quality values range from 0-1
                }
            },
            plugins: {
                legend: { display: false },         // Hide legend since space is limited
                annotation: {
                    annotations: {
                        box: {
                            type: 'box',
                            xMin: config.minRange,
                            xMax: config.maxRange,
                            yMin: config.minNormalized,
                            yMax: config.maxNormalized,
                            backgroundColor: COLORS.annotation,
                            borderWidth: 0
                        }
                    }
                },
                tooltip: {
                    enabled: false,                 // Use custom tooltip
                    external: handleTooltip
                }
            }
        }
    });
}

/**
 * Helper function to create a dataset configuration for Chart.js.
 * 
 * @param {string} label - Name of the dataset
 * @param {Array<number>} data - Array of data points
 * @param {string} yAxisID - ID of the y-axis to use for this dataset
 * @param {string} color - CSS color string for the line
 * @returns {Object} Dataset configuration object
 */
function createDataset(label, data, yAxisID, color) {
    return {
        label,
        yAxisID,
        data,
        borderColor: color,
        borderWidth: 1
    };
}

/**
 * Handler for custom tooltip in the Chart.js visualization.
 * This function is called by Chart.js when hovering over the chart.
 * It updates and positions the custom tooltip with data values and
 * Angstrom resolution values.
 * 
 * @param {Object} context - The Chart.js tooltip context
 */
export function handleTooltip(context) {
    const { chart, tooltip } = context;
    const tooltipEl = chart.canvas.parentNode.querySelector('.custom-tooltip');

    // Hide tooltip if not active
    if (tooltip.opacity === 0) {
        tooltipEl.style.opacity = 0;
        return;
    }

    // Get the data point index and calculate corresponding resolution in Angstroms
    const index = tooltip.dataPoints[0].dataIndex;
    const angstromValue = calculateAngstromValue(index, tooltip.dataPoints[0].dataset.data.length);

    // Update tooltip title with resolution value
    tooltipEl.querySelector('.tooltip-title').textContent = `${angstromValue.toFixed(1)} Å`;

    // Update values for each data series
    tooltip.dataPoints.forEach(point => {
        const dataset = point.dataset;

        switch (dataset.label) {
            case 'Experimental':
                tooltipEl.querySelector('.experimental-value').textContent = point.formattedValue;
                break;
            case 'Fitted':
                tooltipEl.querySelector('.fitted-value').textContent = point.formattedValue;
                break;
            case 'Quality':
                tooltipEl.querySelector('.quality-value').textContent = point.formattedValue;
                break;
        }
    });

    // Show the tooltip
    tooltipEl.style.opacity = 1;
}

/**
 * Hides the custom tooltip.
 * 
 * @param {Chart} chart - The Chart.js instance
 */
function hideTooltip(chart) {
    const tooltipEl = chart.canvas.parentNode.querySelector('.custom-tooltip');
    if (tooltipEl) {
        tooltipEl.style.opacity = 0;
    }
}

/**
 * Calculates the resolution in Angstroms from a pixel-space index.
 * Uses the formula: resolution = (2 * length * pixelSize) / index
 * 
 * @param {number} index - The index in pixel space
 * @param {number} length - The total length of the data array
 * @returns {number} - The resolution in Angstroms
 */
function calculateAngstromValue(index, length) {
    const value = length * 2 / index * binnedPixelSize;
    return parseFloat(value.toFixed(2));
}

/**
 * Gets or creates the custom tooltip element.
 * 
 * @param {Chart} chart - The Chart.js instance
 * @param {number} angstromValue - The resolution in Angstroms to display
 * @returns {HTMLElement} - The tooltip element
 */
function getOrCreateTooltip(chart, angstromValue) {
    let tooltipEl = chart.canvas.parentNode.querySelector('.custom-tooltip');

    // Create tooltip structure if it doesn't exist
    if (!tooltipEl) {
        tooltipEl = document.createElement('div');
        tooltipEl.className = 'custom-tooltip';

        const textEl = document.createElement('div');
        textEl.className = 'tooltip-title';
        tooltipEl.appendChild(textEl);

        const table = document.createElement('table');
        tooltipEl.appendChild(table);

        chart.canvas.parentNode.appendChild(tooltipEl);
    }

    tooltipEl.querySelector('.tooltip-title').textContent = `${angstromValue} Å`;
    return tooltipEl;
}

/**
 * Updates the content of the tooltip with current data values.
 * 
 * @param {HTMLElement} tooltipEl - The tooltip element
 * @param {Object} tooltip - The Chart.js tooltip object
 */
function updateTooltipContent(tooltipEl, tooltip) {
    const tableBody = document.createElement('tbody');

    tooltip.dataPoints.forEach((point, i) => {
        const tr = document.createElement('tr');
        const dataset = point.dataset;
        const color = tooltip.labelColors[i];

        // Label cell
        const labelCell = document.createElement('td');
        const bullet = document.createElement('span');
        bullet.className = 'custom-tooltip-bullet';
        bullet.style.background = color.backgroundColor;
        bullet.style.borderColor = color.borderColor;
        labelCell.appendChild(bullet);
        labelCell.appendChild(document.createTextNode(dataset.label));

        // Value cell
        const valueCell = document.createElement('td');
        valueCell.textContent = point.formattedValue;

        // Normalized value cell
        const normalizedCell = document.createElement('td');
        const normalValue = dataset.label === 'Quality'
            ? point.formattedValue
            : calculateNormalizedValue(dataset.data, point.dataIndex);
        normalizedCell.textContent = normalValue;

        tr.append(labelCell, valueCell, normalizedCell);
        tableBody.appendChild(tr);
    });

    const table = tooltipEl.querySelector('table');
    table.replaceChildren(tableBody);
}

/**
 * Calculates a normalized value (0-1) for a data point.
 * 
 * @param {Array<number>} data - The complete data array
 * @param {number} currentIndex - The index of the current point
 * @returns {string} - The normalized value as a string with 5 decimal places
 */
function calculateNormalizedValue(data, currentIndex) {
    const min = Math.min(...data);
    const max = Math.max(...data);
    const current = data[currentIndex];
    return ((current - min) / (max - min)).toFixed(5);
}

/**
 * Positions the tooltip element on the chart.
 * 
 * @param {HTMLElement} tooltipEl - The tooltip element
 * @param {Chart} chart - The Chart.js instance
 * @param {Object} tooltip - The Chart.js tooltip object
 */
function positionTooltip(tooltipEl, chart, tooltip) {
    const { offsetLeft: positionX, offsetTop: positionY } = chart.canvas;

    tooltipEl.style.opacity = 1;
    tooltipEl.style.left = positionX + tooltip.caretX + 'px';
    tooltipEl.style.top = positionY + 'px';
    tooltipEl.style.font = tooltip.options.bodyFont.string;
    tooltipEl.style.padding = tooltip.options.padding + 'px';
}

/**
 * Destroys the chart and cleans up resources.
 * This should be called when the component is unmounted or when
 * creating a new chart to replace the existing one.
 */
export function destroyChart() {
    if (chart) {
        chart.destroy();
        chart = null;
    }
}