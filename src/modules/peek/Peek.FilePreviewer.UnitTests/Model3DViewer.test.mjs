// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// Run with Node.js 22.15 or later: node --test Model3DViewer.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import test from 'node:test';

const three = new URL('../Peek.FilePreviewer/Assets/Model3D/three/build/three.module.min.js', import.meta.url).href;
registerHooks({
    resolve(specifier, context, nextResolve) {
        return nextResolve(specifier === 'three' ? three : specifier, context);
    },
});

globalThis.window = {};
globalThis.document = { getElementById: () => ({}) };
globalThis.location = { search: '' };

const { parseStl } = await import('../Peek.FilePreviewer/Assets/Model3D/viewer.js');
const { deflateSync, inflateSync, unzipSync, zipSync } = await import('../Peek.FilePreviewer/Assets/Model3D/three/examples/jsm/libs/fflate.module.js');
const sample = readFileSync(new URL('../../previewpane/UnitTests-StlThumbnailProvider/HelperFiles/sample.stl', import.meta.url));
const archiveEntry = '3D/3dmodel.model';
const archiveContents = new TextEncoder().encode('<model>triangles and materials</model>'.repeat(40));

function modelBytes() {
    return Uint8Array.from(sample).buffer;
}

function setAllNormals(buffer, x, y, z) {
    const view = new DataView(buffer);
    const count = view.getUint32(80, true);
    for (let i = 0; i < count; i++) {
        const offset = 84 + (i * 50);
        view.setFloat32(offset, x, true);
        view.setFloat32(offset + 4, y, true);
        view.setFloat32(offset + 8, z, true);
    }
}

function zip64Archive(fields) {
    const archive = Buffer.from(zipSync({ [archiveEntry]: archiveContents }));
    const end = archive.length - 22;
    const central = archive.readUInt32LE(end + 16);
    const extra = Buffer.alloc(4 + (fields.length * 8));
    extra.writeUInt16LE(1, 0);
    extra.writeUInt16LE(fields.length * 8, 2);

    let position = 4;
    for (const [name, offset] of [['size', 24], ['compressedSize', 20], ['offset', 42]]) {
        if (fields.includes(name)) {
            extra.writeBigUInt64LE(BigInt(archive.readUInt32LE(central + offset)), position);
            archive.writeUInt32LE(0xffffffff, central + offset);
            position += 8;
        }
    }

    const insertAt = central + 46 + archive.readUInt16LE(central + 28);
    archive.writeUInt16LE(archive.readUInt16LE(central + 30) + extra.length, central + 30);
    archive.writeUInt32LE(archive.readUInt32LE(end + 12) + extra.length, end + 12);
    return Buffer.concat([archive.subarray(0, insertAt), extra, archive.subarray(insertAt)]);
}

test('ASCII STL with leading whitespace and uppercase keywords loads', () => {
    const text = `  SOLID model
FACET NORMAL 0 0 1
OUTER LOOP
VERTEX 0 0 0
VERTEX 1 0 0
VERTEX 0 1 0
ENDLOOP
ENDFACET
ENDSOLID model`;

    const mesh = parseStl(new TextEncoder().encode(text).buffer);
    assert.equal(mesh.geometry.getAttribute('position').count, 3);
    assert.equal(mesh.geometry.getAttribute('normal').getZ(0), 1);
});

test('binary STL with a solid-prefixed header loads', () => {
    const buffer = modelBytes();
    new Uint8Array(buffer).set(new TextEncoder().encode('solid'));

    assert.equal(parseStl(buffer).geometry.getAttribute('position').count, 136 * 3);
});

test('truncated binary STL is rejected', () => {
    assert.throws(() => parseStl(modelBytes().slice(0, 100)));
});

test('STL facets with zero normals receive usable face normals', () => {
    const normal = parseStl(modelBytes()).geometry.getAttribute('normal');

    assert.ok(Math.abs(normal.getX(0) + 1) < 1e-6);
    assert.ok(Math.abs(normal.getY(0)) < 1e-6);
    assert.ok(Math.abs(normal.getZ(0)) < 1e-6);
});

test('STL facets with invalid normals receive usable face normals', () => {
    const buffer = modelBytes();
    setAllNormals(buffer, 0, 0, 1);
    new DataView(buffer).setFloat32(84, NaN, true);
    const normal = parseStl(buffer).geometry.getAttribute('normal');

    assert.ok(Math.abs(normal.getX(0) + 1) < 1e-6);
    assert.ok(Number.isFinite(normal.getY(0)));
    assert.ok(Number.isFinite(normal.getZ(0)));
});

test('STL files retain valid authored normals', () => {
    const buffer = modelBytes();
    setAllNormals(buffer, 0, 0, 1);
    const normal = parseStl(buffer).geometry.getAttribute('normal');

    assert.equal(normal.getX(0), 0);
    assert.equal(normal.getY(0), 0);
    assert.equal(normal.getZ(0), 1);
});

test('ZIP64 entries with independently optional metadata decompress correctly', () => {
    for (const fields of [
        ['size'],
        ['compressedSize'],
        ['offset'],
        ['size', 'offset'],
        ['size', 'compressedSize', 'offset'],
    ]) {
        assert.deepEqual(unzipSync(zip64Archive(fields))[archiveEntry], archiveContents);
    }
});

test('ZIP64 entries reject truncated extra metadata', () => {
    const archive = zip64Archive(['size']);
    const end = archive.length - 22;
    const central = archive.readUInt32LE(end + 16);
    const extra = central + 46 + archive.readUInt16LE(central + 28);
    archive.writeUInt16LE(4, extra + 2);

    assert.throws(() => unzipSync(archive), { code: 13 });
});

test('ZIP entries cannot expand beyond their declared size', () => {
    const archive = Buffer.from(zipSync({ [archiveEntry]: archiveContents }));
    const central = archive.readUInt32LE(archive.length - 22 + 16);
    archive.writeUInt32LE(archiveContents.length - 1, central + 24);

    assert.throws(() => unzipSync(archive), { code: 13 });
});

test('direct inflate retains the supplied-output-buffer behavior', () => {
    const output = new Uint8Array(1);

    assert.deepEqual(inflateSync(deflateSync(archiveContents), { out: output }), archiveContents.subarray(0, 1));
});
