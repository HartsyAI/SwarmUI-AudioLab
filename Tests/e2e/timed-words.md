# E2E: timed words (API level)

Needs a dev server built with an engine that has word timestamps (HartsyInference alpha.441 or later) and Whisper `base`
(`openai/whisper-base`) in the engine's model cache. Get a session id with `POST /API/GetNewSession`, build the request body
from a short vocal clip (`base64 -w0 clip.wav`), and post it to the route. A clip of words spoken one at a time, with the
voiced burst of each measured from its energy, makes the times easy to judge.

| # | Request | Expect |
|---|---------|--------|
| 1 | `AudioLabAlignLyrics` with `lyrics` and `align_model: "base"` | `success`, `source: "aligned"`, one entry per lyric word, `start` < `end`, in order, each over its voiced burst, `duration` = clip length |
| 2 | `AudioLabAlignLyrics` with no `lyrics` | `source: "stt"`, the transcript's words timed (Whisper times its own) |
| 3 | `ProcessSTT` with `word_timestamps: true` | `words` filled, same times as row 2 |
| 4 | `ProcessSTT` without `word_timestamps` | `words` is `[]` and `duration` is added; the rest of the response is unchanged |
| 5 | `AudioLabAlignLyrics` with `align_model: "nope"` | `success: false`, `error_code: "unknown_model"`, the available models listed |
| 6 | `AudioLabAlignLyrics` with lyrics on a clip over 30 s | An error naming the single-window limit |
| 7 | `AudioLabAlignLyrics` with a transcriber that returns no words (no word timing) | `source: "stt+aligned"`: its text is timed by the aligner |
| 8 | Lyrics with line breaks and blank lines | Words from different lines stay separate; times in order |
| 9 | `AudioLabAlignLyrics` with `lyrics` and `align_provider_id: "moonshine_stt"` | `success: false`, `error_code: "no_word_timing"` |
| 10 | `ProcessSTT` with `align_text` and no `word_timestamps` | The text is timed (align_text implies word_timestamps) |

Pass criteria: every row behaves as stated and the server log shows no errors.

## Run log

2026-11-18, Debug dev build against a local build of HartsyInference PR #964 (alpha.441), GPU (RTX 3060), `whisper_stt` `base`,
13-word clip (16.4 s). Rows 1-5 passed: row 1 and row 2 gave the times from the engine's own real-weights test (e.g. `quick`
0.26-1.54 aligned, 0.44-1.50 transcribed), row 4 returned `words: []`, row 5 returned the model list. Not run: rows 6-8
(no moonshine or other non-timing model weights on this machine; rows 6 and 8 need long or multi-line clips). Rows 9-10 were added
after the run and have not been run.
