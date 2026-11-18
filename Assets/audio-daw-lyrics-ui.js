/**
 * Audio Lab DAW: the lyric strip and the Lyrics tab.
 *
 * The strip sits between the transport and the arrangement and shows the line being sung with the sung part of each word
 * filled in, and the line after it. The Lyrics tab edits the selected clip's lyrics, times the words with a speech model, nudges
 * the timing, and reads and writes lyric files. Everything about words and times lives in `AudioDawLyrics` (pure); the DAW
 * state, undo and the network calls are the host's, reached through the callbacks passed to `create`.
 *
 * Callbacks: `getClips()` all clips; `getAudible()` (optional) a `Map` of clip id to the timeline intervals where it is heard; `getSelectedClip()` -> `{ clip, track }` or null; `findClip(id)` -> `{ clip, track }`;
 * `commit(clipId, lyrics|null, label)` (undo step + store + refresh); `align(request)` -> the API result;
 * `seek(seconds)`; `listSttEngines()` -> `[{ id, name, models: [{ id, name }] }]`; `notify(text, kind)`.
 */
const AudioDawLyricsUi = (() => {
    'use strict';

    /** localStorage key for whether the strip is shown. */
    const STRIP_KEY = 'audiolab_daw_lyric_strip';

    /** localStorage key for the speech models chosen on the Lyrics tab. */
    const MODELS_KEY = 'audiolab_daw_lyric_models';

    /** The provider used to time words when the user has not picked one (Whisper: its attention yields word times). */
    const DEFAULT_PROVIDER = 'whisper_stt';

    /** Most word rows the tab lists, so a long song does not build thousands of inputs. */
    const MAX_ROWS = 400;

    /** A fill change smaller than this fraction is not repainted. */
    const FILL_STEP = 0.02;

    /** The slot classes a strip line moves between: gone above, the line being sung, the line after it, and waiting below. */
    const SLOTS = ['pos-prev', 'pos-current', 'pos-next', 'pos-after'];

    /** Stands for "no line is laid out yet" while the anchor is compared. */
    const NO_ANCHOR = -99;

    /** Read a stored JSON value, tolerating blocked or empty storage. */
    function readStored(key, fallback) {
        try {
            const raw = localStorage.getItem(key);
            return raw === null ? fallback : JSON.parse(raw);
        }
        catch (_) {
            return fallback;
        }
    }

    /** Store a JSON value, ignoring blocked storage. */
    function writeStored(key, value) {
        try {
            localStorage.setItem(key, JSON.stringify(value));
        }
        catch (_) {
            // Storage can be blocked (private window); the choice just is not remembered
        }
    }

    /** The status sentence for words flagged unsure: empty when there are none. */
    function unsureNote(lyrics) {
        const n = AudioDawLyrics.unsureCount(lyrics);
        return n ? `. ${n} ` + translate('words look unsung (squeezed together by the aligner) and are left off the strip; use Transcribe instead if the singer left lines out') : '';
    }

    /** Hand text to the browser as a file. */
    function download(name, text, mime) {
        const url = URL.createObjectURL(new Blob([text], { type: mime }));
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    /**
     * Build the strip and the tab controller.
     * @param {HTMLElement} stripHost - where the strip element is inserted (before `before`)
     * @param {HTMLElement} before - the element the strip goes in front of
     * @param {Object} host - the callbacks described above
     */
    function create(stripHost, before, host) {
        const strip = createDiv(null, 'daw-lyric-strip');
        strip.hidden = true;
        stripHost.insertBefore(strip, before);

        let timeline = { words: [], lines: [] };
        let shown = readStored(STRIP_KEY, true) !== false;
        let renderedAnchor = NO_ANCHOR; // the line in the "current" slot as last laid out (NO_ANCHOR: nothing laid out yet)
        let renderedIdle = false;
        let renderedTime = NaN; // the time `syncTime` last worked out; the same time again does nothing
        let lastTime = 0;
        const lineEls = new Map(); // line index -> { el, spans, fills }: one element per line, moved between slots as the song goes on
        const at = { current: -1, next: -1 }; // reused every frame, so a frame that changes nothing allocates nothing
        const fills = [];
        let panel = null;

        /** Build the element for a timeline line: a span per word (clicking one seeks to it), every fill at 0. */
        function buildLine(index) {
            const el = createDiv(null, 'daw-lyric-line');
            const spans = [];
            for (const word of AudioDawLyrics.wordsOfLine(timeline, timeline.lines[index])) {
                const span = createSpan(null, 'daw-lyric-word');
                span.textContent = word.w + ' ';
                span.addEventListener('click', () => host.seek(word.start));
                el.appendChild(span);
                spans.push(span);
            }
            return { el, spans, fills: spans.map(() => 0) };
        }

        /** Paint every word of a line to the same fill (a finished line is whole, a waiting one empty). */
        function setAllFills(entry, value) {
            for (let i = 0; i < entry.spans.length; i++) {
                if (entry.fills[i] !== value) {
                    entry.fills[i] = value;
                    entry.spans[i].style.setProperty('--fill', `${value * 100}%`);
                }
            }
        }

        /** The slot class for the line `rel` places from the anchor: above (gone), current, next, or waiting below. */
        function slotOf(rel, idle) {
            if (rel < 0 || (rel === 0 && idle)) return 'pos-prev';
            return rel === 0 ? 'pos-current' : (rel === 1 ? 'pos-next' : 'pos-after');
        }

        /**
         * Put the lines in their slots around `anchor`. A line that is already on screen keeps its element and just changes slot,
         * so it scrolls (and keeps its fill) instead of being redrawn; one entering starts in the slot below its target. A jump
         * of more than one line (a seek) lays out without the slide.
         */
        function layoutLines(anchor, idle, instant) {
            strip.classList.toggle('daw-lyric-jump', instant);
            for (const [index, entry] of lineEls) {
                if (index < anchor - 1 || index > anchor + 2 || instant) {
                    entry.el.remove();
                    lineEls.delete(index);
                }
            }
            for (let index = Math.max(0, anchor - 1); index <= anchor + 2 && index < timeline.lines.length; index++) {
                const rel = index - anchor;
                let entry = lineEls.get(index);
                if (!entry) {
                    entry = buildLine(index);
                    entry.el.classList.add(rel < 0 ? 'pos-prev' : 'pos-after');
                    strip.appendChild(entry.el);
                    lineEls.set(index, entry);
                    entry.el.offsetWidth; // commit the starting slot so the move to the real one is animated
                }
                const slot = slotOf(rel, idle);
                for (const name of SLOTS) {
                    entry.el.classList.toggle(name, name === slot);
                }
                if (slot === 'pos-prev') setAllFills(entry, 1);
                else if (slot !== 'pos-current') setAllFills(entry, 0);
            }
            if (instant) {
                strip.offsetWidth;
                strip.classList.remove('daw-lyric-jump');
            }
        }

        /** Show the strip only when there are timed words and the user wants it. */
        function applyVisibility() {
            strip.hidden = !(shown && timeline.words.length > 0);
        }

        /** Drop what `syncTime` remembers and every line on screen, so its next call lays out from scratch. */
        function forceRepaint() {
            for (const entry of lineEls.values()) entry.el.remove();
            lineEls.clear();
            renderedAnchor = NO_ANCHOR;
            renderedIdle = false;
            renderedTime = NaN;
        }

        /** Rebuild the timeline from the project's clips (call after any clip or lyric change). */
        function refresh() {
            timeline = AudioDawLyrics.buildTimeline(host.getClips(), host.getAudible?.() || null);
            forceRepaint();
            applyVisibility();
            syncTime(lastTime);
            panel?.update();
        }

        /** Per-frame hook: scroll the lines to where `time` puts them and fill the sung part of the current one. */
        function syncTime(time) {
            lastTime = time;
            if (strip.hidden || time === renderedTime) return;
            renderedTime = time;
            AudioDawLyrics.lineAt(timeline, time, undefined, at);
            // Between lines the line just finished stays the anchor (scrolled up), so the next one waits below until it starts
            const anchor = at.current >= 0 ? at.current : at.next - 1;
            const idle = at.current < 0;
            if (anchor !== renderedAnchor || idle !== renderedIdle) {
                const instant = renderedAnchor === NO_ANCHOR || Math.abs(anchor - renderedAnchor) > 1;
                renderedAnchor = anchor;
                renderedIdle = idle;
                layoutLines(anchor, idle, instant);
            }
            const entry = idle ? null : lineEls.get(anchor);
            if (!entry) return;
            AudioDawLyrics.fillOf(timeline, timeline.lines[anchor], time, fills);
            for (let i = 0; i < entry.spans.length && i < fills.length; i++) {
                if (Math.abs(fills[i] - entry.fills[i]) >= FILL_STEP || ((fills[i] === 0 || fills[i] === 1) && fills[i] !== entry.fills[i])) {
                    entry.fills[i] = fills[i];
                    entry.spans[i].style.setProperty('--fill', `${(fills[i] * 100).toFixed(0)}%`);
                }
            }
        }

        /** Show or hide the strip and remember the choice. */
        function setShown(on) {
            shown = !!on;
            writeStored(STRIP_KEY, shown);
            forceRepaint();
            applyVisibility();
            syncTime(lastTime);
            panel?.update();
        }

        /** The Lyrics tab: built once into `pane`, rebound to the selected clip by `update`. */
        function renderPanel(pane) {
            pane.textContent = '';
            const root = createDiv(null, 'daw-lyrics-panel');
            pane.appendChild(root);
            const stored = readStored(MODELS_KEY, {}) || {};
            const choice = { sttProvider: stored.sttProvider || '', sttModel: stored.sttModel || '', alignProvider: stored.alignProvider || '', alignModel: stored.alignModel || '' };

            const head = createDiv(null, 'daw-lyrics-head');
            const title = createSpan(null, 'daw-lyrics-title');
            const showRow = document.createElement('label');
            showRow.className = 'daw-lyrics-show';
            const showBox = document.createElement('input');
            showBox.type = 'checkbox';
            showBox.addEventListener('change', () => setShown(showBox.checked));
            showRow.appendChild(showBox);
            showRow.appendChild(document.createTextNode(' ' + translate('Show the lyric strip')));
            head.appendChild(title);
            head.appendChild(showRow);
            root.appendChild(head);

            const body = createDiv(null, 'daw-lyrics-body');
            root.appendChild(body);
            const left = createDiv(null, 'daw-lyrics-left');
            const right = createDiv(null, 'daw-lyrics-right');
            body.appendChild(left);
            body.appendChild(right);

            const area = document.createElement('textarea');
            area.className = 'daw-generate-text daw-lyrics-text';
            area.rows = 4;
            area.placeholder = translate('Lyrics for the selected clip. Section tags like [Verse 1] are fine; each line is a lyric line.');
            left.appendChild(area);

            // Two separate model choices: what hears the words, and what times them.
            const mkPicker = (labelText, providerKey, modelKey) => {
                const row = createDiv(null, 'daw-lyrics-model-row');
                const label = createSpan(null, 'daw-lyrics-model-label');
                label.textContent = labelText;
                const providerSel = document.createElement('select');
                providerSel.className = 'daw-lyrics-select';
                const modelSel = document.createElement('select');
                modelSel.className = 'daw-lyrics-select';
                row.append(label, providerSel, modelSel);
                const fill = () => {
                    const engines = host.listSttEngines();
                    // The engine list loads after the tab is built: until it is here, keep the remembered choice
                    if (!engines.length) {
                        return;
                    }
                    providerSel.textContent = '';
                    for (const e of engines) providerSel.add(new Option(e.name, e.id));
                    if (!engines.some(e => e.id === choice[providerKey])) {
                        choice[providerKey] = engines.find(e => e.id === DEFAULT_PROVIDER)?.id || engines[0]?.id || '';
                    }
                    providerSel.value = choice[providerKey];
                    const models = engines.find(e => e.id === providerSel.value)?.models || [];
                    modelSel.textContent = '';
                    modelSel.add(new Option(translate('Default model'), ''));
                    for (const m of models) modelSel.add(new Option(m.name, m.id));
                    if (!models.some(m => m.id === choice[modelKey])) choice[modelKey] = '';
                    modelSel.value = choice[modelKey];
                };
                providerSel.addEventListener('change', () => {
                    choice[providerKey] = providerSel.value;
                    choice[modelKey] = '';
                    writeStored(MODELS_KEY, choice);
                    fill();
                });
                modelSel.addEventListener('change', () => {
                    choice[modelKey] = modelSel.value;
                    writeStored(MODELS_KEY, choice);
                });
                return { row, fill };
            };
            const aligner = mkPicker(translate('Times the words'), 'alignProvider', 'alignModel');
            const transcriber = mkPicker(translate('Hears the words'), 'sttProvider', 'sttModel');
            left.appendChild(aligner.row);
            left.appendChild(transcriber.row);

            const status = createDiv(null, 'daw-lyrics-status');
            const buttons = createDiv(null, 'daw-lyrics-buttons');
            const mkBtn = (text, tip, onClick) => {
                const b = document.createElement('button');
                b.type = 'button';
                b.className = 'basic-button btn-sm';
                b.textContent = text;
                b.title = tip;
                b.addEventListener('click', onClick);
                buttons.appendChild(b);
                return b;
            };

            /** The clip the tab is bound to, looked up fresh (undo can replace the objects). */
            let boundId = null;
            const bound = () => (boundId ? host.findClip(boundId) : null);

            /** Run the time-the-words job: align the typed lyrics, or transcribe when there are none. */
            let aligning = false;
            const runAlign = async (forceTranscribe) => {
                const found = bound();
                if (!found || aligning) return;
                aligning = true;
                const clipId = found.clip.id;
                const text = forceTranscribe ? '' : area.value;
                const hasText = AudioDawLyrics.wordsOf(text).length > 0;
                status.textContent = hasText ? translate('Timing the lyrics…') : translate('Listening…');
                alignBtn.disabled = transcribeBtn.disabled = true;
                try {
                    const result = await host.align({
                        // Section tags like [Chorus] are for the reader, not the singer: the aligner gets only the sung lines
                        clipId, lyrics: hasText ? AudioDawLyrics.linesOf(text).join('\n') : '',
                        sttProvider: choice.sttProvider, sttModel: choice.sttModel,
                        alignProvider: choice.alignProvider, alignModel: choice.alignModel
                    });
                    if (!result?.success) throw new Error(result?.error || 'No result');
                    // The clip that was timed, even when the selection has moved on while the model worked
                    const now = host.findClip(clipId);
                    if (!now) return;
                    const opts = { offset: now.clip.offset || 0, model: result.model || '' };
                    const apiWords = result.words || [];
                    if (!apiWords.length) throw new Error(translate('No words were heard in the clip'));
                    const lyrics = hasText
                        ? AudioDawLyrics.fromAlignment(text, apiWords, { ...opts, source: result.source })
                        : AudioDawLyrics.fromTranscript(apiWords, { ...opts, source: result.source });
                    host.commit(now.clip.id, lyrics, translate('Time lyrics'));
                    status.textContent = translate('Timed') + ` ${lyrics.words.length - AudioDawLyrics.unsureCount(lyrics)} ` + translate('words') + ` (${result.source})` + unsureNote(lyrics);
                }
                catch (err) {
                    console.error('[AudioDaw] Lyric timing failed:', err);
                    status.textContent = translate('Timing failed:') + ' ' + err.message;
                    host.notify(translate('Timing failed:') + ' ' + err.message, 'notice-pop-red');
                }
                finally {
                    aligning = false;
                    alignBtn.disabled = transcribeBtn.disabled = !bound();
                }
            };
            const alignBtn = mkBtn(translate('Time the words'), translate('Time the typed lyrics against the clip (or listen to the clip when none are typed)'), () => runAlign(false));
            const transcribeBtn = mkBtn(translate('Transcribe instead'), translate('Ignore the typed lyrics and write them from what the clip sounds like'), () => runAlign(true));
            mkBtn(translate('Estimate timing'), translate('Spread the typed lyrics evenly over the clip: rough, but needs no speech model'), () => {
                const found = bound();
                if (!found) return;
                const c = found.clip;
                const visible = c.duration - (c.offset || 0) - (c.trimEnd || 0);
                const words = AudioDawLyrics.estimate(area.value, c.offset || 0, (c.offset || 0) + visible);
                if (!words.length) {
                    status.textContent = translate('Type some lyrics first');
                    return;
                }
                host.commit(c.id, AudioDawLyrics.create({ text: area.value, words, source: 'estimated' }), translate('Estimate lyric timing'));
            });
            mkBtn(translate('Clear timing'), translate('Keep the text, drop the word times'), () => {
                const found = bound();
                const lyrics = found?.clip.meta?.lyrics;
                if (!lyrics) return;
                host.commit(found.clip.id, AudioDawLyrics.create({ ...lyrics, words: [], source: 'manual' }), translate('Clear lyric timing'));
            });
            left.appendChild(buttons);
            left.appendChild(status);

            const nudgeRow = createDiv(null, 'daw-lyrics-buttons');
            const nudge = (dt) => {
                const found = bound();
                const lyrics = found?.clip.meta?.lyrics;
                if (!AudioDawLyrics.isTimed(lyrics)) return;
                host.commit(found.clip.id, AudioDawLyrics.shift(lyrics, dt), translate('Nudge lyrics'));
            };
            for (const [label, dt] of [['−0.25 s', -0.25], ['−50 ms', -0.05], ['+50 ms', 0.05], ['+0.25 s', 0.25]]) {
                const b = document.createElement('button');
                b.type = 'button';
                b.className = 'basic-button btn-sm';
                b.textContent = label;
                b.title = translate('Move every word of this clip earlier or later');
                b.addEventListener('click', () => nudge(dt));
                nudgeRow.appendChild(b);
            }
            left.appendChild(nudgeRow);

            const fileRow = createDiv(null, 'daw-lyrics-buttons');
            const fileInput = document.createElement('input');
            fileInput.type = 'file';
            fileInput.accept = '.lrc,.txt';
            fileInput.hidden = true;
            fileInput.addEventListener('change', async () => {
                const file = fileInput.files?.[0];
                fileInput.value = '';
                const found = bound();
                if (!file || !found) return;
                const c = found.clip;
                const lyrics = AudioDawLyrics.fromLrc(await file.text(), { toSource: (t) => t - c.startTime + (c.offset || 0) });
                if (!lyrics) {
                    host.notify(translate('That file has no timed lyric lines'), 'notice-pop-yellow');
                    return;
                }
                host.commit(c.id, lyrics, translate('Import lyrics'));
            });
            const mkFile = (text, tip, onClick) => {
                const b = document.createElement('button');
                b.type = 'button';
                b.className = 'basic-button btn-sm';
                b.textContent = text;
                b.title = tip;
                b.addEventListener('click', onClick);
                fileRow.appendChild(b);
            };
            // A muted clip (its vocal stem plays instead) still exports its own lyrics
            const exportTimeline = () => AudioDawLyrics.buildTimeline(bound() ? [{ ...bound().clip, muted: false }] : []);
            mkFile(translate('Import LRC…'), translate('Read timed lyric lines from an .lrc file; its times are on the project timeline'), () => fileInput.click());
            mkFile(translate('Export LRC'), translate('Save this clip\'s lyrics with word times as an .lrc file'), () => {
                const tl = exportTimeline();
                if (tl.words.length) download('lyrics.lrc', AudioDawLyrics.toLrc(tl, { words: true }), 'text/plain');
            });
            mkFile(translate('Export SRT'), translate('Save this clip\'s lyrics as subtitles'), () => {
                const tl = exportTimeline();
                if (tl.words.length) download('lyrics.srt', AudioDawLyrics.toSrt(tl), 'text/plain');
            });
            fileRow.appendChild(fileInput);
            left.appendChild(fileRow);

            const table = createDiv(null, 'daw-lyrics-words');
            right.appendChild(table);

            area.addEventListener('change', () => {
                const found = bound();
                if (!found) return;
                const prev = found.clip.meta?.lyrics || AudioDawLyrics.create();
                const next = AudioDawLyrics.setText(prev, area.value);
                if (!area.value.trim()) {
                    host.commit(found.clip.id, null, translate('Clear lyrics'));
                    return;
                }
                host.commit(found.clip.id, next, translate('Edit lyrics'));
            });

            /** One row per word, with start and end as timeline seconds (editing converts back to the clip's source time). */
            const renderWords = (clip) => {
                table.textContent = '';
                const lyrics = clip?.meta?.lyrics;
                if (!lyrics?.words?.length) {
                    const empty = createDiv(null, 'daw-lyrics-empty');
                    empty.textContent = translate('No word times yet. Time the words, estimate them, or import an LRC file.');
                    table.appendChild(empty);
                    return;
                }
                const offset = clip.offset || 0;
                const toTimeline = (s) => clip.startTime + (s - offset);
                const toSource = (t) => t - clip.startTime + offset;
                lyrics.words.slice(0, MAX_ROWS).forEach((word, index) => {
                    const row = createDiv(null, 'daw-lyrics-word-row' + (word.u ? ' daw-lyrics-unsure' : ''));
                    const name = createSpan(null, 'daw-lyrics-word-text');
                    name.textContent = word.w;
                    name.title = word.u ? translate('Probably not sung here: the aligner had no audio to give this word, so the strip skips it. Set a time to keep it.') : translate('Click to jump here');
                    name.addEventListener('click', () => host.seek(toTimeline(word.s)));
                    row.appendChild(name);
                    for (const edge of ['s', 'e']) {
                        const input = document.createElement('input');
                        input.type = 'number';
                        input.step = '0.05';
                        input.className = 'daw-lyrics-time';
                        input.value = toTimeline(word[edge]).toFixed(2);
                        input.title = edge === 's' ? translate('Start (timeline seconds)') : translate('End (timeline seconds)');
                        input.addEventListener('change', () => {
                            const found = bound();
                            const cur = found?.clip.meta?.lyrics;
                            const t = parseFloat(input.value);
                            if (!cur || !Number.isFinite(t)) return;
                            host.commit(found.clip.id, AudioDawLyrics.setWordTime(cur, index, { [edge]: toSource(t) }), translate('Edit word time'));
                        });
                        row.appendChild(input);
                    }
                    table.appendChild(row);
                });
                if (lyrics.words.length > MAX_ROWS) {
                    const more = createDiv(null, 'daw-lyrics-empty');
                    more.textContent = `+${lyrics.words.length - MAX_ROWS} ` + translate('more words (use the nudge buttons or split the clip to edit them)');
                    table.appendChild(more);
                }
            };

            /** Rebind to the selected clip. */
            const update = () => {
                const sel = host.getSelectedClip();
                boundId = sel?.clip?.id || null;
                showBox.checked = shown;
                aligner.fill();
                transcriber.fill();
                title.textContent = sel ? `${translate('Lyrics')}: ${sel.clip.name}` : translate('Select an audio clip to edit its lyrics');
                const lyrics = sel?.clip.meta?.lyrics;
                if (document.activeElement !== area) area.value = lyrics?.text || '';
                area.disabled = !sel;
                for (const b of buttons.querySelectorAll('button')) b.disabled = !sel;
                renderWords(sel?.clip);
                const count = (lyrics?.words?.length || 0) - AudioDawLyrics.unsureCount(lyrics);
                status.textContent = lyrics?.words?.length ? `${count} ` + translate('timed words') + (lyrics.source ? ` (${lyrics.source}${lyrics.model ? ', ' + lyrics.model : ''})` : '') + unsureNote(lyrics) : '';
            };
            panel = { update };
            update();
        }

        return {
            refresh, syncTime, setShown, renderPanel, isShown: () => shown,
            updatePanel: () => panel?.update(),
            destroy: () => strip.remove()
        };
    }

    return { create };
})();
