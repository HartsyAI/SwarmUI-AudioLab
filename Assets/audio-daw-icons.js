/**
 * Audio Lab DAW: track icons.
 *
 * A track icon is either a short string (an emoji) or a small image stored as a `data:` URL, so it saves inside the
 * project with no extra files. This module holds the preset set, the rule for what a stored icon may be, the one
 * place that draws an icon into an element, and the image downscale for uploads. The checks are pure; only `fill` and
 * `fromImageFile` touch the DOM.
 */
const AudioDawIcons = (() => {
    'use strict';

    /** Presets shown in the picker. */
    const PRESETS = [
        '\u{1F3A4}', '\u{1F399}️', '\u{1F3A7}', '\u{1F3B8}', '\u{1F3B9}', '\u{1F941}', '\u{1F3BB}', '\u{1F3BA}',
        '\u{1F3B7}', '\u{1FA95}', '\u{1F3BC}', '\u{1F3B5}', '\u{1F50A}', '\u{1F514}', '\u{1F916}', '\u{1F464}',
        '\u{1F465}', '\u{1F30A}', '\u{1F525}', '⚡', '\u{1F319}', '⭐', '\u{1F4A5}', '\u{1F338}'
    ];

    /** Longest an icon string may be: room for a joined emoji sequence, not for text. */
    const MAX_TEXT_LENGTH = 16;

    /** Longest an image data URL may be, in characters (a 48 px icon is a few thousand). */
    const MAX_IMAGE_LENGTH = 60000;

    /** Edge, in pixels, an uploaded image is cropped and scaled to. */
    const IMAGE_SIZE = 48;

    /** Whether a stored icon is an image (a data URL) rather than text. */
    function isImage(icon) {
        return typeof icon === 'string' && icon.startsWith('data:');
    }

    /**
     * The icon to keep, or null: a short string, or a png, jpeg or webp data URL of sane size. Anything else (a
     * remote URL, an svg, a script-ish string, an oversize image) is dropped, so a hand-edited project cannot put
     * arbitrary content in a header.
     */
    function sanitize(value) {
        if (typeof value !== 'string' || !value) return null;
        if (isImage(value)) {
            if (value.length > MAX_IMAGE_LENGTH) return null;
            return /^data:image\/(png|jpeg|webp);base64,[A-Za-z0-9+/]+=*$/.test(value) ? value : null;
        }
        return value.length <= MAX_TEXT_LENGTH ? value : null;
    }

    /** Draw an icon into an element (text for an emoji, an img for an image). Passing no icon empties it. */
    function fill(el, icon) {
        el.textContent = '';
        const ok = sanitize(icon);
        if (!ok) return;
        if (isImage(ok)) {
            const img = document.createElement('img');
            img.src = ok;
            img.alt = '';
            img.draggable = false;
            el.appendChild(img);
        }
        else {
            el.textContent = ok;
        }
    }

    /**
     * Turn an image file into a small square data URL: centre-cropped, scaled to IMAGE_SIZE, webp where the browser
     * writes it and png otherwise.
     * @param {Blob} file
     * @returns {Promise<string>} the data URL (rejects if the file is not a readable image)
     */
    async function fromImageFile(file) {
        const bitmap = await createImageBitmap(file);
        try {
            const side = Math.min(bitmap.width, bitmap.height);
            const canvas = document.createElement('canvas');
            canvas.width = IMAGE_SIZE;
            canvas.height = IMAGE_SIZE;
            const ctx = canvas.getContext('2d');
            ctx.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, IMAGE_SIZE, IMAGE_SIZE);
            let url = canvas.toDataURL('image/webp', 0.85);
            if (!url.startsWith('data:image/webp')) url = canvas.toDataURL('image/png');
            const ok = sanitize(url);
            if (!ok) throw new Error('The image is too large to use as an icon');
            return ok;
        }
        finally {
            if (bitmap.close) bitmap.close();
        }
    }

    return { PRESETS, IMAGE_SIZE, isImage, sanitize, fill, fromImageFile };
})();
