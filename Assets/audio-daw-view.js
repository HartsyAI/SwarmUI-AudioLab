/**
 * Audio Lab DAW: viewport math.
 *
 * Pure functions over plain numbers (no DOM, no DAW state) that decide where the horizontal scroll of the clip lanes
 * should go: following the playhead while playing, and revealing a position after a seek. Kept separate from the
 * DAW shell so other views (overview strip, lyrics lane) can reuse the same rules, and so the rules can be exercised
 * from a browser console without a project: `AudioDawView.followTarget({...})`.
 *
 * All positions are pixels in the scrolling lane's content space (time * zoom).
 */
const AudioDawView = (() => {
    'use strict';

    /** Follow modes. 'off' never scrolls, 'page' jumps a page when the playhead leaves, 'smooth' glides it. */
    const FOLLOW_MODES = ['off', 'page', 'smooth'];

    /** Fraction of the view the playhead rests at while gliding in smooth mode. */
    const SMOOTH_ANCHOR = 0.35;

    /** Page mode leaves this fraction of the view (at most LEAD_MAX px) behind the playhead after a jump. */
    const PAGE_LEAD = 0.1;
    const LEAD_MAX = 80;

    /** Normalize a stored or user-supplied mode to one of FOLLOW_MODES. */
    function normalizeMode(mode, fallback = 'page') {
        return FOLLOW_MODES.includes(mode) ? mode : fallback;
    }

    /** The largest scrollLeft that still shows content: scrollWidth - viewW, never negative. */
    function maxScroll(viewW, scrollWidth) {
        if (!(scrollWidth > viewW)) return 0;
        return scrollWidth - viewW;
    }

    /** Clamp a scrollLeft into [0, maxScroll]. A missing scrollWidth leaves only the lower bound. */
    function clampScroll(value, viewW, scrollWidth) {
        const hi = Number.isFinite(scrollWidth) ? maxScroll(viewW, scrollWidth) : Infinity;
        return Math.max(0, Math.min(value, hi));
    }

    /**
     * Where to scroll so a playing playhead stays on screen, or null to leave the view alone.
     * @param {Object} o
     * @param {number} o.scrollLeft - current scroll position
     * @param {number} o.viewW - visible width; 0 means the view is hidden, so nothing moves
     * @param {number} o.playheadX - playhead position in content space
     * @param {string} o.mode - 'off' | 'page' | 'smooth'
     * @param {number} [o.scrollWidth] - total content width, to clamp against
     * @returns {number|null} the new scrollLeft, or null for no change
     */
    function followTarget({ scrollLeft, viewW, playheadX, mode, scrollWidth }) {
        if (mode !== 'page' && mode !== 'smooth') return null;
        if (!(viewW > 0) || !Number.isFinite(playheadX) || !Number.isFinite(scrollLeft)) return null;
        const right = scrollLeft + viewW;
        const offscreen = playheadX < scrollLeft || playheadX > right;
        let target;
        if (mode === 'page') {
            if (!offscreen) return null;
            target = playheadX - Math.min(LEAD_MAX, viewW * PAGE_LEAD);
        }
        else {
            // Glide: hold the playhead at the anchor once it passes it; a jump (loop wrap, seek) off screen recenters
            const anchor = viewW * SMOOTH_ANCHOR;
            if (offscreen) target = playheadX - anchor;
            else if (playheadX - scrollLeft > anchor) target = playheadX - anchor;
            else return null;
        }
        target = clampScroll(target, viewW, scrollWidth);
        // A sub-pixel difference is noise; skipping it avoids a scroll event per frame while nothing moves
        return Math.abs(target - scrollLeft) < 0.5 ? null : target;
    }

    /**
     * Where to scroll so an explicit position (after a seek or a marker jump) is visible, or null if it already is.
     * Unlike followTarget this applies in every mode, because a jump to a place off screen should show it.
     * @param {Object} o
     * @param {number} o.scrollLeft
     * @param {number} o.viewW
     * @param {number} o.x - the position to reveal, in content space
     * @param {number} [o.margin] - pixels of breathing room kept inside either edge
     * @param {number} [o.scrollWidth]
     * @returns {number|null}
     */
    function revealTarget({ scrollLeft, viewW, x, margin = 24, scrollWidth }) {
        if (!(viewW > 0) || !Number.isFinite(x) || !Number.isFinite(scrollLeft)) return null;
        const m = Math.min(margin, viewW / 4);
        if (x >= scrollLeft + m && x <= scrollLeft + viewW - m) return null;
        // Land a quarter of the way in, so what comes next is visible rather than the playhead on the edge
        const target = clampScroll(x - viewW * 0.25, viewW, scrollWidth);
        return Math.abs(target - scrollLeft) < 0.5 ? null : target;
    }

    /**
     * Which scrollbar of a scroller a press landed on: 'h' (horizontal), 'v' (vertical) or null for the content.
     * A press on a scrollbar reports the scroller itself as its target, so it looks like a click on empty space.
     * @param {number} clientX - pointer position, viewport space
     * @param {number} clientY
     * @param {{left: number, top: number}} rect - the scroller's bounding rect
     * @param {number} clientWidth - the scroller's content width (excludes the vertical scrollbar)
     * @param {number} clientHeight - the scroller's content height (excludes the horizontal scrollbar)
     */
    function scrollbarHit(clientX, clientY, rect, clientWidth, clientHeight) {
        if (clientY - rect.top > clientHeight) return 'h';
        if (clientX - rect.left > clientWidth) return 'v';
        return null;
    }

    return { FOLLOW_MODES, normalizeMode, maxScroll, clampScroll, followTarget, revealTarget, scrollbarHit };
})();
