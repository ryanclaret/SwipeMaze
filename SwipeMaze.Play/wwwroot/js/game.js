// Swipe Maze (web): paints the maze on a canvas, turns touch / mouse / keyboard into swipes, taps and
// pinches, and plays sounds. All the rules live in SwipeMaze.Core (C#); this file only draws what it is
// told to and reports what the player did. Mirrors MazeDrawable.cs and the touch handling in MainPage.xaml.cs.

// ---- local storage, safe when the browser refuses it (private window, storage turned off) ----
window.store = {
    get(key) { try { return localStorage.getItem(key); } catch (e) { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); return true; } catch (e) { return false; } },
    remove(key) { try { localStorage.removeItem(key); } catch (e) { } },
};

window.game = (() => {
    const SWIPE = 20;   // px a finger must travel to count as a swipe
    const TAP = 12;     // (the tap slack lives in C#; kept here for reference)

    const COLORS = {
        paper: '#ffffff', wall: '#1b5e20', trail: '#a5d6a7', startTint: '#c8e6c9', finishTint: '#ffcdd2',
        startArrow: '#2e7d32', finishArrow: '#c62828', avatarFill: '#ffb300', avatarEdge: '#e65100',
        face: '#3e2723', highlight: '#1e88e5',
    };
    const FIREWORK_COLORS = ['#ffc107', '#f44336', '#42a5f5', '#66bb6a', '#ec407a', '#ab47bc'];
    const LABEL_SIZE = 15;

    let dotnet = null, canvas = null, ctx = null;

    // the maze being shown (set once per maze) and where it was last drawn (so touches map back to squares)
    let cols = 0, rows = 0, cells = [], mazeId = 0;
    let layout = { cell: 1, left: 0, top: 0, zoom: 1, x: 0, y: 0 };
    let walls = null, wallsKey = '';
    let lastAvatar = [0, 0];

    // ---------------------------------------------------------------- drawing

    function buildWalls(cell, left, top) {
        const p = new Path2D();
        const width = cell * cols, height = cell * rows, startCol = cols >> 1, finishCol = cols >> 1;
        const line = (x1, y1, x2, y2) => { p.moveTo(x1, y1); p.lineTo(x2, y2); };
        for (let r = 0; r < rows; r++) {
            const y = top + r * cell;
            line(left, y, left, y + cell);                    // left border
            line(left + width, y, left + width, y + cell);    // right border
        }
        for (let c = 0; c < cols; c++) {
            const x = left + c * cell;
            if (c !== startCol) line(x, top, x + cell, top);                       // top border, minus the start opening
            if (c !== finishCol) line(x, top + height, x + cell, top + height);    // bottom border, minus the finish opening
        }
        // Direction bits as in Maze.cs: Up 1, Right 2, Down 4, Left 8.
        for (let r = 0; r < rows; r++) {
            for (let c = 0; c < cols; c++) {
                const bits = cells[r * cols + c], x = left + c * cell, y = top + r * cell;
                if (c < cols - 1 && !(bits & 2)) line(x + cell, y, x + cell, y + cell);
                if (r < rows - 1 && !(bits & 4)) line(x, y + cell, x + cell, y + cell);
            }
        }
        return p;
    }

    function drawArrow(x, tipY, cell, color) {
        const size = Math.max(cell, 6);
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.moveTo(x - size * 0.35, tipY - size * 0.6);
        ctx.lineTo(x + size * 0.35, tipY - size * 0.6);
        ctx.lineTo(x, tipY);
        ctx.closePath();
        ctx.fill();
    }

    // A word beside an arrow, kept the same size on screen at any zoom; at zoom 1 it shrinks to fit if tight.
    function drawLabel(text, centerX, edgeY, limitY, above, color, zoom) {
        let size = LABEL_SIZE / zoom;
        if (zoom <= 1.001) {
            const room = above ? edgeY - limitY : limitY - edgeY;
            size = Math.min(size, room / 1.4);
            if (size < 6) return;
        }
        ctx.font = `bold ${size}px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`;
        ctx.fillStyle = color;
        ctx.textAlign = 'center';
        ctx.textBaseline = above ? 'bottom' : 'top';
        ctx.fillText(text, centerX, edgeY);
    }

    function drawAvatar(x, y, cell) {
        const r = Math.max(cell * 0.38, 2);
        ctx.fillStyle = COLORS.avatarFill;
        ctx.strokeStyle = COLORS.avatarEdge;
        ctx.lineWidth = Math.max(0.8, r * 0.16);
        ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill(); ctx.stroke();

        const eye = Math.max(0.6, r * 0.16);
        ctx.fillStyle = COLORS.face;
        ctx.beginPath(); ctx.arc(x - r * 0.34, y - r * 0.2, eye, 0, Math.PI * 2); ctx.fill();
        ctx.beginPath(); ctx.arc(x + r * 0.34, y - r * 0.2, eye, 0, Math.PI * 2); ctx.fill();

        ctx.strokeStyle = COLORS.face;
        ctx.lineWidth = Math.max(0.5, r * 0.12);
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(x - r * 0.4, y + r * 0.18);
        ctx.quadraticCurveTo(x, y + r * 0.72, x + r * 0.4, y + r * 0.18);
        ctx.stroke();
    }

    /**
     * Paints one frame. Everything arrives as plain numbers and arrays (nothing the .NET trimmer could strip):
     *   layout: cell, left, top, zoom, x, y   (the maze at zoom 1 is cell-sized squares from (left, top); the
     *            view is then scaled by zoom and moved by (x, y))
     *   trail: flat [col, row, col, row, ...] from the start to the avatar
     *   avatarCol/avatarRow, highlightCol/highlightRow (-1 for none)
     *   fireworks: flat [x, y, size, colorIndex, alpha, ...] in screen units, or an empty array
     */
    function draw(cell, left, top, zoom, x, y, trail, avatarCol, avatarRow, highlightCol, highlightRow, fireworks) {
        if (!ctx || cols === 0) return;
        const dpr = window.devicePixelRatio || 1;
        const w = canvas.clientWidth, h = canvas.clientHeight;
        if (canvas.width !== Math.round(w * dpr)) canvas.width = Math.round(w * dpr);
        if (canvas.height !== Math.round(h * dpr)) canvas.height = Math.round(h * dpr);
        layout = { cell, left, top, zoom, x, y };
        lastAvatar = [avatarCol, avatarRow];

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);

        ctx.save();
        ctx.translate(x, y);
        ctx.scale(zoom, zoom);

        const width = cell * cols, height = cell * rows;
        const cx = c => left + (c + 0.5) * cell, cy = r => top + (r + 0.5) * cell;
        const startCol = cols >> 1, finishCol = cols >> 1;

        ctx.fillStyle = COLORS.paper;
        ctx.fillRect(left, top, width, height);
        ctx.fillStyle = COLORS.startTint; ctx.fillRect(left + startCol * cell, top, cell, cell);
        ctx.fillStyle = COLORS.finishTint; ctx.fillRect(left + finishCol * cell, top + (rows - 1) * cell, cell, cell);

        // the trail walked so far
        if (trail.length >= 4) {
            ctx.strokeStyle = COLORS.trail;
            ctx.lineWidth = cell * 0.45;
            ctx.lineCap = 'round'; ctx.lineJoin = 'round';
            ctx.beginPath();
            for (let i = 0; i < trail.length; i += 2) {
                const px = cx(trail[i]), py = cy(trail[i + 1]);
                i === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py);
            }
            ctx.stroke();
        }

        // walls (kept between frames: building them means visiting every square)
        const key = `${mazeId}|${cell}|${left}|${top}`;
        if (key !== wallsKey) { walls = buildWalls(cell, left, top); wallsKey = key; }
        ctx.strokeStyle = COLORS.wall;
        ctx.lineWidth = Math.max(1, cell * 0.11);   // thin on the big mazes, or the corridors would vanish
        ctx.lineCap = 'round';
        ctx.stroke(walls);

        const startTip = top - cell * 0.15, finishTip = top + height + cell * 0.85;
        drawArrow(cx(startCol), startTip, cell, COLORS.startArrow);
        drawArrow(cx(finishCol), finishTip, cell, COLORS.finishArrow);
        const arrowHeight = 0.6 * Math.max(cell, 6);
        drawLabel('Start', cx(startCol), startTip - arrowHeight - 2 / zoom, 0, true, COLORS.startArrow, zoom);
        drawLabel('Finish', cx(finishCol), finishTip + 2 / zoom, h, false, COLORS.finishArrow, zoom);

        drawAvatar(cx(avatarCol), cy(avatarRow), cell);

        if (highlightCol >= 0) {
            ctx.strokeStyle = COLORS.highlight;
            ctx.lineWidth = Math.max(2, cell * 0.15);
            ctx.beginPath();
            ctx.arc(cx(highlightCol), cy(highlightRow), Math.max(cell * 0.7, 7), 0, Math.PI * 2);
            ctx.stroke();
        }
        ctx.restore();

        // fireworks sit on top, in screen units (outside the zoom)
        for (let i = 0; i + 4 < fireworks.length; i += 5) {
            ctx.globalAlpha = Math.max(0, Math.min(1, fireworks[i + 4]));
            ctx.fillStyle = FIREWORK_COLORS[fireworks[i + 3] | 0] || '#ffc107';
            ctx.beginPath(); ctx.arc(fireworks[i], fireworks[i + 1], fireworks[i + 2], 0, Math.PI * 2); ctx.fill();
        }
        ctx.globalAlpha = 1;
    }

    // ---------------------------------------------------------------- input

    const pointers = new Map();          // pointerId -> {x, y}
    let start = null;                    // where a single finger began
    let multi = false, pinching = false, pinchStart = 1;

    function local(ev) {
        const r = canvas.getBoundingClientRect();
        return { x: ev.clientX - r.left, y: ev.clientY - r.top };
    }

    function pinchPoints() {
        const [a, b] = [...pointers.values()];
        return { midX: (a.x + b.x) / 2, midY: (a.y + b.y) / 2, dist: Math.max(Math.hypot(a.x - b.x, a.y - b.y), 1) };
    }

    function handlePinch() {
        multi = true;
        const p = pinchPoints();
        if (!pinching) {
            pinching = true;
            pinchStart = p.dist;
            dotnet.invokeMethod('OnPinchStart', p.midX, p.midY);
        } else {
            dotnet.invokeMethod('OnPinch', p.midX, p.midY, p.dist / pinchStart);
        }
    }

    // Swipe direction as the C# Direction enum: Up 0, Right 1, Down 2, Left 3. The longer axis wins.
    function directionFor(dx, dy) {
        return Math.abs(dx) >= Math.abs(dy) ? (dx > 0 ? 1 : 3) : (dy > 0 ? 2 : 0);
    }

    function onDown(ev) {
        unlockAudio();
        if (ev.pointerType === 'mouse' && ev.button !== 0) return;
        try { canvas.setPointerCapture(ev.pointerId); } catch (e) { /* keeps tracking the finger past the canvas edge; optional */ }
        pointers.set(ev.pointerId, local(ev));
        if (pointers.size === 1) { start = local(ev); multi = false; pinching = false; }
        if (pointers.size >= 2) handlePinch();
    }

    function onMove(ev) {
        if (!pointers.has(ev.pointerId)) return;
        pointers.set(ev.pointerId, local(ev));
        if (pointers.size >= 2) handlePinch();
        else pinching = false;     // one finger lifted: a second going down again is a fresh pinch
    }

    function onUp(ev) {
        if (!pointers.has(ev.pointerId)) return;
        const end = local(ev);
        pointers.delete(ev.pointerId);

        if (multi) {
            // whatever the fingers did was zooming/panning; the last finger leaving is not a swipe
            if (pointers.size === 0) { multi = false; pinching = false; }
            return;
        }
        if (pointers.size > 0 || !start) return;

        const dx = end.x - start.x, dy = end.y - start.y;
        start = null;
        if (Math.max(Math.abs(dx), Math.abs(dy)) >= SWIPE) {
            dotnet.invokeMethodAsync('OnSwipe', directionFor(dx, dy));
        } else {
            // a tap: tell C# which square (in cell units, the centre of square (3,4) being 3.0, 4.0)
            const wx = (end.x - layout.x) / layout.zoom, wy = (end.y - layout.y) / layout.zoom;
            const col = (wx - layout.left) / layout.cell - 0.5, row = (wy - layout.top) / layout.cell - 0.5;
            dotnet.invokeMethodAsync('OnTap', col, row, layout.cell * layout.zoom);
        }
    }

    function onCancel(ev) {
        pointers.delete(ev.pointerId);
        if (pointers.size === 0) { multi = false; pinching = false; start = null; }
    }

    // ---------------------------------------------------------------- sound (Web Audio)

    let actx = null;
    const pcm = {}, buffers = {}, voices = {};

    function unlockAudio() {
        try {
            if (!actx) {
                const Ctx = window.AudioContext || window.webkitAudioContext;
                if (!Ctx) return null;
                actx = new Ctx();
            }
            if (actx.state === 'suspended') actx.resume();   // browsers only allow this from a touch or click
        } catch (e) { return null; }
        return actx;
    }

    // ---------------------------------------------------------------- public API

    return {
        init(ref, canvasId) {
            dotnet = ref;
            canvas = document.getElementById(canvasId);
            ctx = canvas.getContext('2d');

            canvas.addEventListener('pointerdown', onDown);
            canvas.addEventListener('pointermove', onMove);
            canvas.addEventListener('pointerup', onUp);
            canvas.addEventListener('pointercancel', onCancel);
            canvas.addEventListener('contextmenu', e => e.preventDefault());

            // mouse wheel zooms around the pointer (a PC's pinch)
            canvas.addEventListener('wheel', e => {
                e.preventDefault();
                const p = local(e);
                dotnet.invokeMethod('OnWheel', p.x, p.y, Math.exp(-e.deltaY * 0.0015));
            }, { passive: false });

            // arrow keys / WASD move; Escape answers a question with "no"
            document.addEventListener('keydown', e => {
                if (e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA')) return;
                if (e.key === 'Escape') { dotnet.invokeMethodAsync('OnEscape'); return; }
                const map = { ArrowUp: 0, ArrowRight: 1, ArrowDown: 2, ArrowLeft: 3, w: 0, d: 1, s: 2, a: 3, W: 0, D: 1, S: 2, A: 3 };
                if (!(e.key in map)) return;
                e.preventDefault();
                dotnet.invokeMethodAsync('OnSwipe', map[e.key]);
            });

            // taps on buttons and the like also unlock audio
            document.addEventListener('pointerdown', unlockAudio, true);

            // tell C# how big the maze area is, now and whenever it changes (rotation, address bar, window size)
            const report = () => {
                const r = canvas.getBoundingClientRect();
                dotnet.invokeMethod('OnResize', r.width, r.height);
            };
            new ResizeObserver(report).observe(canvas);
            report();
        },

        // How big the maze area is right now, in CSS pixels (0 x 0 while it is hidden)
        size() { const r = canvas.getBoundingClientRect(); return [r.width, r.height]; },

        setMaze(c, r, data) { cols = c; rows = r; cells = data; mazeId++; wallsKey = ''; },

        draw,

        // Read-only peek at the current maze, so automated tests can work out a route and drive the game.
        debugMaze() { return { cols, rows, cells, avatar: lastAvatar, layout }; },

        blur() { if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); },

        focus(id) { const el = document.getElementById(id); if (el) el.focus(); },

        // 16-bit little-endian mono samples at 22050 Hz, base64
        loadSound(name, b64) {
            const bin = atob(b64), n = bin.length >> 1, f = new Float32Array(n);
            for (let i = 0; i < n; i++) {
                let v = bin.charCodeAt(2 * i) | (bin.charCodeAt(2 * i + 1) << 8);
                if (v >= 32768) v -= 65536;
                f[i] = v / 32768;
            }
            pcm[name] = f;
        },

        // Plays now, cutting off an earlier play of the same sound (except the firework pop, which may overlap)
        playSound(name) {
            const c = unlockAudio(), samples = pcm[name];
            if (!c || !samples) return;
            let buffer = buffers[name];
            if (!buffer) {
                buffer = c.createBuffer(1, samples.length, 22050);
                buffer.copyToChannel(samples, 0);
                buffers[name] = buffer;
            }
            if (name !== 'Pop' && voices[name]) { try { voices[name].stop(); } catch (e) { } }
            const source = c.createBufferSource();
            source.buffer = buffer;
            source.connect(c.destination);
            source.start();
            voices[name] = source;
        },
    };
})();
