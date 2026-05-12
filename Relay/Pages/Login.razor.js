let animationId = null;
let blobs = [];

const BOUNDS_MIN = -15;
const BOUNDS_MAX = 65;
const EDGE_ZONE = 20;
const EDGE_FORCE = 0.0003;

function rand(min, max) {
    return Math.random() * (max - min) + min;
}

function pickTarget(blob) {
    // Keep targets well inside bounds so blobs don't aim for edges
    blob.tx = rand(0, 50);
    blob.ty = rand(0, 50);
}

function edgePush(pos, min, max) {
    // Smooth repulsion force near boundaries
    const distToMin = pos - min;
    const distToMax = max - pos;
    let force = 0;
    if (distToMin < EDGE_ZONE) {
        force = (EDGE_ZONE - distToMin) / EDGE_ZONE;
        return force * force * EDGE_FORCE;
    }
    if (distToMax < EDGE_ZONE) {
        force = (EDGE_ZONE - distToMax) / EDGE_ZONE;
        return -force * force * EDGE_FORCE;
    }
    return 0;
}

export function start() {
    const container = document.querySelector('.metaballs-bg');
    if (!container) return;

    const elements = container.querySelectorAll('.metaball');
    blobs = Array.from(elements).map(el => {
        const obj = {
            el,
            x: rand(5, 45),
            y: rand(5, 45),
            vx: 0,
            vy: 0,
            tx: 0,
            ty: 0,
            maxSpeed: rand(0.015, 0.03),
            steer: rand(0.0004, 0.001),
        };
        pickTarget(obj);
        return obj;
    });

    function animate() {
        for (const b of blobs) {
            // Steer velocity toward target
            const dx = b.tx - b.x;
            const dy = b.ty - b.y;

            b.vx += dx * b.steer;
            b.vy += dy * b.steer;

            // Soft boundary repulsion
            b.vx += edgePush(b.x, BOUNDS_MIN, BOUNDS_MAX);
            b.vy += edgePush(b.y, BOUNDS_MIN, BOUNDS_MAX);

            // Dampen to keep it smooth and under max speed
            const speed = Math.sqrt(b.vx * b.vx + b.vy * b.vy);
            if (speed > b.maxSpeed) {
                b.vx *= b.maxSpeed / speed;
                b.vy *= b.maxSpeed / speed;
            }
            b.vx *= 0.995;
            b.vy *= 0.995;

            b.x += b.vx;
            b.y += b.vy;

            // Pick new target when close
            if (dx * dx + dy * dy < 25) {
                pickTarget(b);
            }

            b.el.style.transform = `translate(${b.x}vw, ${b.y}vh)`;
        }
        animationId = requestAnimationFrame(animate);
    }

    animationId = requestAnimationFrame(animate);
}

export function stop() {
    if (animationId != null) {
        cancelAnimationFrame(animationId);
        animationId = null;
    }
    blobs = [];
}
