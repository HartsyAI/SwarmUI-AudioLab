/**
 * Audio Lab DAW: track groups (folders).
 *
 * A group is a named, colored set of tracks that sit next to each other in the arrangement. It collapses to one row,
 * and its mute, solo and volume apply to every member. This module is pure logic over plain arrays: no DOM, no DAW
 * state, no audio nodes. The shell asks it for the row layout, the effective mute and solo of a track, and the group
 * gain, and keeps the persistence and menus itself. It can be exercised from a browser console:
 * `AudioDawGroups.layoutRows(tracks, groups)`.
 *
 * Group shape: { id, name, color, collapsed, tint (its clips draw in the group color), muted, soloed, volume }
 * A track joins a group with `track.groupId`. Members are kept contiguous in the track list, in the group's place.
 * Groups do not nest.
 */
const AudioDawGroups = (() => {
    'use strict';

    let seq = 0;

    /** The fallback color for a group that has none. */
    const DEFAULT_COLOR = '#7a8cff';

    /** A new unique id. */
    function newId() {
        return `group-${Date.now().toString(36)}-${++seq}`;
    }

    /** A default group name like "Group 2", unused so far (the word is translated when the page has `translate`). */
    function uniqueName(groups, base = 'Group') {
        const word = typeof translate === 'function' ? translate(base) : base;
        const used = new Set((groups || []).map(g => g.name));
        for (let i = (groups || []).length + 1; ; i++) {
            const name = `${word} ${i}`;
            if (!used.has(name)) return name;
        }
    }

    /** A new group. Pass the existing list so the default name is unique. */
    function create({ name, color } = {}, groups = []) {
        return {
            id: newId(),
            name: (name && name.trim()) || uniqueName(groups),
            color: color || DEFAULT_COLOR,
            collapsed: false,
            tint: true,
            muted: false,
            soloed: false,
            volume: 1
        };
    }

    /** The group a track belongs to, or null. */
    function groupOf(track, groups) {
        if (!track || !track.groupId) return null;
        return (groups || []).find(g => g.id === track.groupId) || null;
    }

    /** The members of a group, in track order. */
    function membersOf(group, tracks) {
        if (!group) return [];
        return (tracks || []).filter(t => t.groupId === group.id);
    }

    /**
     * Put the list in order: every group's members sit together where its first member was, and groups with no
     * members (or members pointing at a group that does not exist) are dropped. Returns new arrays and leaves the
     * inputs untouched: `{ tracks, groups }`. Track objects are the same references, with a dangling `groupId`
     * cleared on them.
     */
    function normalize(tracks, groups) {
        const known = new Set((groups || []).map(g => g.id));
        const live = (tracks || []).slice();
        for (const t of live) {
            if (t.groupId && !known.has(t.groupId)) t.groupId = null;
        }
        const placed = new Set();
        const out = [];
        for (const t of live) {
            if (!t.groupId) {
                out.push(t);
                continue;
            }
            if (placed.has(t.groupId)) continue;
            placed.add(t.groupId);
            for (const m of live) {
                if (m.groupId === t.groupId) out.push(m);
            }
        }
        return { tracks: out, groups: (groups || []).filter(g => placed.has(g.id)) };
    }

    /**
     * The rows the arrangement draws, top to bottom: a group row before its members, then each track. Members of a
     * collapsed group are still listed (the shell hides them) with `hidden: true`, so every track keeps its header
     * and lane. `index` is the track's position in the whole list, so track numbers do not change when grouped.
     * @returns {Array<{kind: 'group', group: object, members: object[]} | {kind: 'track', track: object, index: number, group: object|null, hidden: boolean}>}
     */
    function layoutRows(tracks, groups) {
        const rows = [];
        const seen = new Set();
        (tracks || []).forEach((track, index) => {
            const group = groupOf(track, groups);
            if (group && !seen.has(group.id)) {
                seen.add(group.id);
                rows.push({ kind: 'group', group, members: membersOf(group, tracks) });
            }
            rows.push({ kind: 'track', track, index, group, hidden: !!(group && group.collapsed) });
        });
        return rows;
    }

    /** Whether a track is silent by its own mute or its group's. */
    function effectiveMuted(track, groups) {
        return !!track.muted || !!groupOf(track, groups)?.muted;
    }

    /** Whether a track counts as soloed: soloed itself, or in a soloed group. */
    function effectiveSoloed(track, groups) {
        return !!track.soloed || !!groupOf(track, groups)?.soloed;
    }

    /** The group's volume as a gain multiplier on each member (1 when the track is in no group). */
    function groupGain(track, groups) {
        const g = groupOf(track, groups);
        if (!g) return 1;
        return Number.isFinite(g.volume) ? Math.max(0, g.volume) : 1;
    }

    /** Put the tracks with these ids into a group (null takes them out). Normalizes; returns `{ tracks, groups }`. */
    function assign(tracks, groups, trackIds, groupId) {
        const ids = new Set(trackIds || []);
        for (const t of tracks || []) {
            if (ids.has(t.id)) t.groupId = groupId || null;
        }
        return normalize(tracks, groups);
    }

    /**
     * The track list as movable blocks, top to bottom: an ungrouped track is a block of one, a group is one block
     * holding its members. Moving a block never splits a group.
     */
    function blocks(tracks) {
        const out = [];
        const byGroup = new Map();
        for (const t of tracks || []) {
            if (!t.groupId) {
                out.push({ groupId: null, tracks: [t] });
                continue;
            }
            let b = byGroup.get(t.groupId);
            if (!b) {
                b = { groupId: t.groupId, tracks: [] };
                byGroup.set(t.groupId, b);
                out.push(b);
            }
            b.tracks.push(t);
        }
        return out;
    }

    /**
     * Move a track up (-1) or down (+1). Inside a group it swaps with its neighbor in the group and stops at the
     * ends; outside, it steps over the next block (a whole group counts as one). Returns a new track list, or null
     * when it cannot move.
     */
    function moveTrack(tracks, trackId, dir) {
        const track = (tracks || []).find(t => t.id === trackId);
        if (!track || !dir) return null;
        const bs = blocks(tracks);
        const bi = bs.findIndex(b => b.tracks.includes(track));
        if (track.groupId) {
            const members = bs[bi].tracks;
            const i = members.indexOf(track);
            const j = i + (dir > 0 ? 1 : -1);
            if (j < 0 || j >= members.length) return null;
            [members[i], members[j]] = [members[j], members[i]];
        }
        else {
            const j = bi + (dir > 0 ? 1 : -1);
            if (j < 0 || j >= bs.length) return null;
            [bs[bi], bs[j]] = [bs[j], bs[bi]];
        }
        return bs.flatMap(b => b.tracks);
    }

    /** Move a whole group up or down past the neighboring block. Returns a new track list, or null at the ends. */
    function moveGroup(tracks, groupId, dir) {
        const bs = blocks(tracks);
        const bi = bs.findIndex(b => b.groupId === groupId);
        if (bi < 0 || !dir) return null;
        const j = bi + (dir > 0 ? 1 : -1);
        if (j < 0 || j >= bs.length) return null;
        [bs[bi], bs[j]] = [bs[j], bs[bi]];
        return bs.flatMap(b => b.tracks);
    }

    /**
     * The spans a group's summary bar draws: one {start, end} per audio clip (its visible part) and per MIDI clip.
     * @param {object[]} members - the group's tracks
     * @param {function(object): number} [midiLength] - the length of a MIDI clip, in seconds
     */
    function summarySpans(members, midiLength = null) {
        const spans = [];
        for (const t of members || []) {
            for (const c of t.clips || []) {
                const len = (c.duration || 0) - (c.offset || 0) - (c.trimEnd || 0);
                if (len > 0) spans.push({ start: c.startTime || 0, end: (c.startTime || 0) + len, muted: !!c.muted });
            }
            for (const m of t.midiClips || []) {
                const len = midiLength ? midiLength(m) : (m.length || 0);
                if (len > 0) spans.push({ start: m.startTime || 0, end: (m.startTime || 0) + len, muted: !!m.muted });
            }
        }
        return spans;
    }

    /** Plain copies carrying exactly the persisted fields. */
    function serialize(groups) {
        return (groups || []).map(g => ({
            id: g.id, name: g.name, color: g.color || null, collapsed: !!g.collapsed, tint: g.tint !== false,
            muted: !!g.muted, soloed: !!g.soloed, volume: Number.isFinite(g.volume) ? g.volume : 1
        }));
    }

    /** Read groups back from a saved project or an undo snapshot, tolerating a missing list or bad entries. */
    function restore(raw) {
        if (!Array.isArray(raw)) return [];
        const seen = new Set();
        const out = [];
        for (const r of raw) {
            if (!r || typeof r !== 'object') continue;
            let id = typeof r.id === 'string' && r.id ? r.id : newId();
            if (seen.has(id)) id = newId();
            seen.add(id);
            out.push({
                id,
                name: typeof r.name === 'string' && r.name.trim() ? r.name : uniqueName(out),
                color: typeof r.color === 'string' && r.color ? r.color : DEFAULT_COLOR,
                collapsed: !!r.collapsed,
                tint: r.tint !== false,
                muted: !!r.muted,
                soloed: !!r.soloed,
                volume: Number.isFinite(Number(r.volume)) ? Math.max(0, Number(r.volume)) : 1
            });
        }
        return out;
    }

    return {
        newId, uniqueName, create, groupOf, membersOf, normalize, layoutRows, effectiveMuted, effectiveSoloed, groupGain,
        assign, blocks, moveTrack, moveGroup, summarySpans, serialize, restore
    };
})();
