/**
 * AudioDawStore — IndexedDB persistence for DAW projects.
 * Stores project arrangement JSON in 'projects' (keyPath name) and clip audio
 * Blobs in 'blobs' (keyPath key, namespaced "<project>//<blobKey>").
 * Used for the crash-safe autosave slot and local quick-saves; explicit
 * server-side saves go through the AudioLabSaveProject API instead.
 */
const AudioDawStore = (() => {
    'use strict';

    const DB_NAME = 'audiolab_daw';
    const DB_VERSION = 1;
    let dbPromise = null;

    function openDb() {
        if (dbPromise) return dbPromise;
        dbPromise = new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains('projects')) {
                    db.createObjectStore('projects', { keyPath: 'name' });
                }
                if (!db.objectStoreNames.contains('blobs')) {
                    db.createObjectStore('blobs', { keyPath: 'key' });
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => { dbPromise = null; reject(req.error); };
        });
        return dbPromise;
    }

    function asPromise(req) {
        return new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function blobRange(name) {
        const prefix = name + '//';
        return { prefix, range: IDBKeyRange.bound(prefix, prefix + '\uffff') };
    }

    /**
     * Save (overwrite) a project.
     * @param {string} name - project slot name
     * @param {Object} project - serialized arrangement (JSON-safe)
     * @param {Map<string, Blob>} blobs - blobKey -> audio Blob
     */
    async function saveProject(name, project, blobs) {
        const db = await openDb();
        const { prefix, range } = blobRange(name);
        return new Promise((resolve, reject) => {
            const t = db.transaction(['projects', 'blobs'], 'readwrite');
            t.oncomplete = () => resolve();
            t.onerror = () => reject(t.error);
            t.onabort = () => reject(t.error);
            t.objectStore('projects').put({ name, savedAt: Date.now(), project });
            const bs = t.objectStore('blobs');
            // Replace this project's blob set wholesale
            const delReq = bs.delete(range);
            delReq.onsuccess = () => {
                for (const [key, blob] of blobs) {
                    bs.put({ key: prefix + key, blob });
                }
            };
        });
    }

    /** Load a project. @returns {Promise<{savedAt, project, blobs: Map}|null>} */
    async function loadProject(name) {
        const db = await openDb();
        const t = db.transaction(['projects', 'blobs'], 'readonly');
        const rec = await asPromise(t.objectStore('projects').get(name));
        if (!rec) return null;
        const { prefix, range } = blobRange(name);
        const rows = await asPromise(t.objectStore('blobs').getAll(range));
        const blobs = new Map();
        for (const r of rows) {
            blobs.set(r.key.slice(prefix.length), r.blob);
        }
        return { savedAt: rec.savedAt, project: rec.project, blobs };
    }

    /** Metadata only (no blobs). @returns {Promise<{name, savedAt}|null>} */
    async function getProjectMeta(name) {
        const db = await openDb();
        const rec = await asPromise(db.transaction('projects', 'readonly').objectStore('projects').get(name));
        return rec ? { name: rec.name, savedAt: rec.savedAt } : null;
    }

    /** @returns {Promise<Array<{name, savedAt}>>} newest first */
    async function listProjects() {
        const db = await openDb();
        const rows = await asPromise(db.transaction('projects', 'readonly').objectStore('projects').getAll());
        return rows.map(r => ({ name: r.name, savedAt: r.savedAt })).sort((a, b) => b.savedAt - a.savedAt);
    }

    async function deleteProject(name) {
        const db = await openDb();
        const { range } = blobRange(name);
        return new Promise((resolve, reject) => {
            const t = db.transaction(['projects', 'blobs'], 'readwrite');
            t.oncomplete = () => resolve();
            t.onerror = () => reject(t.error);
            t.objectStore('projects').delete(name);
            t.objectStore('blobs').delete(range);
        });
    }

    return { saveProject, loadProject, getProjectMeta, listProjects, deleteProject };
})();
