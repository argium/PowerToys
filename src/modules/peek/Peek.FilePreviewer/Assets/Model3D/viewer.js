// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

import * as THREE from 'three';
import { OrbitControls } from './three/examples/jsm/controls/OrbitControls.js';
import { unzipSync } from './three/examples/jsm/libs/fflate.module.js';
import { STLLoader } from './three/examples/jsm/loaders/STLLoader.js';
import { ThreeMFLoader } from './three/examples/jsm/loaders/3MFLoader.js';

const canvas = document.getElementById('model');
const notify = (state) => window.chrome?.webview?.postMessage(state);

/**
 * Parses an STL file into a displayable mesh.
 * @param {ArrayBuffer} buffer The STL file contents.
 * @returns {THREE.Mesh} The model mesh.
 * @throws {Error} If the file does not contain valid triangles.
 */
export function parseStl(buffer) {
    const bytes = new Uint8Array(buffer);
    const view = new DataView(buffer);
    const faces = buffer.byteLength >= 84 ? view.getUint32(80, true) : 0;
    const binaryLength = 84 + (faces * 50);
    const hasBinaryHeader = bytes.subarray(0, 84).includes(0);
    let data;

    if (faces > 0 && binaryLength <= buffer.byteLength && (binaryLength === buffer.byteLength || hasBinaryHeader)) {
        // A binary STL can start with "solid". Trim any trailing data so STLLoader
        // recognises it as binary without trusting a malformed facet count.
        data = binaryLength === buffer.byteLength ? buffer : buffer.slice(0, binaryLength);
    } else {
        const text = new TextDecoder().decode(buffer).trimStart();
        if (!/^solid(?:\s|$)/i.test(text)) {
            throw new Error('Invalid STL header or facet count.');
        }

        data = text.replace(/\b(solid|endsolid|facet|endfacet|normal|vertex|outer|loop|endloop)\b/gi, (word) => word.toLowerCase());
    }

    const geometry = new STLLoader().parse(data);
    const positions = geometry.getAttribute('position');
    if (!positions || positions.count < 3 || positions.count % 3 !== 0 ||
        !positions.array.every(Number.isFinite)) {
        throw new Error('The STL does not contain valid triangles.');
    }

    // STLLoader duplicates each facet's normal; rebuild them if an exporter omits or corrupts one.
    const normals = geometry.getAttribute('normal').array;
    for (let i = 0; i < normals.length; i += 9) {
        const x = normals[i];
        const y = normals[i + 1];
        const z = normals[i + 2];
        if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z) ||
            (x === 0 && y === 0 && z === 0)) {
            geometry.computeVertexNormals();
            break;
        }
    }

    const material = new THREE.MeshPhongMaterial({
        color: geometry.hasColors ? 0xffffff : 0x9aa9bc,
        vertexColors: Boolean(geometry.hasColors),
        opacity: geometry.hasColors ? geometry.alpha : 1,
        transparent: Boolean(geometry.hasColors && geometry.alpha < 1),
        side: THREE.DoubleSide,
    });
    return new THREE.Mesh(geometry, material);
}

async function parse3Mf(buffer, limit) {
    let expanded = 0;
    let compressed = 0;
    let entries = 0;
    // Check the same ZIP metadata that ThreeMFLoader's decompressor will use.
    unzipSync(new Uint8Array(buffer), {
        filter: (entry) => {
            if (!Number.isSafeInteger(entry.originalSize) || !Number.isSafeInteger(entry.size) ||
                entry.originalSize > limit - expanded || entry.size > limit - compressed) {
                throw new Error('The 3MF archive exceeds the preview limit.');
            }

            expanded += entry.originalSize;
            compressed += entry.size;
            entries++;
            return false;
        },
    });
    if (entries === 0) {
        throw new Error('The 3MF archive is empty.');
    }

    const manager = new THREE.LoadingManager();
    let textureFailed = false;
    const texturesLoaded = new Promise((resolve) => {
        manager.onLoad = resolve;
        manager.onError = () => { textureFailed = true; };
    });

    manager.itemStart('model');
    let model;
    try {
        model = new ThreeMFLoader(manager).parse(buffer);
    } finally {
        manager.itemEnd('model');
    }

    await texturesLoaded;
    if (textureFailed) {
        throw new Error('A 3MF texture could not be loaded.');
    }

    return model;
}

async function showModel() {
    const parameters = new URLSearchParams(location.search);
    const format = parameters.get('format');
    if (format !== 'stl' && format !== '3mf') {
        throw new Error('Unsupported model format.');
    }

    const response = await fetch(`./model.${format}`);
    if (!response.ok) {
        throw new Error('The model could not be read.');
    }

    const buffer = await response.arrayBuffer();
    const limit = Number(parameters.get('maxExpanded'));
    if (format === '3mf' && (!Number.isSafeInteger(limit) || limit <= 0)) {
        throw new Error('Invalid 3MF preview limit.');
    }

    const model = format === 'stl' ? parseStl(buffer) : await parse3Mf(buffer, limit);
    model.rotation.x = -Math.PI / 2;
    model.updateMatrixWorld(true);

    let hasMesh = false;
    model.traverse((child) => {
        if (child.isMesh && child.geometry?.getAttribute('position')?.count >= 3) {
            hasMesh = true;
        }
    });

    const bounds = new THREE.Box3().setFromObject(model);
    const sphere = bounds.getBoundingSphere(new THREE.Sphere());
    if (!hasMesh || !Number.isFinite(sphere.radius) || sphere.radius <= 0 ||
        ![sphere.center.x, sphere.center.y, sphere.center.z].every(Number.isFinite)) {
        throw new Error('The model does not contain visible geometry.');
    }

    const scene = new THREE.Scene();
    scene.add(model);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x525969, 2));
    const light = new THREE.DirectionalLight(0xffffff, 2);
    light.position.set(1, 2, 3);
    scene.add(light);

    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    const camera = new THREE.PerspectiveCamera(45, 1, sphere.radius / 1000, sphere.radius * 100);
    const controls = new OrbitControls(camera, canvas);
    controls.target.copy(sphere.center);
    controls.minDistance = sphere.radius / 20;

    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    const verticalFov = THREE.MathUtils.degToRad(camera.fov);
    const horizontalFov = 2 * Math.atan(Math.tan(verticalFov / 2) * (width / height));
    const distance = (sphere.radius * 1.15) / Math.sin(Math.min(verticalFov, horizontalFov) / 2);
    controls.maxDistance = Math.max(sphere.radius * 50, distance * 2);
    camera.position.copy(sphere.center).add(new THREE.Vector3(1, 1, 1).normalize().multiplyScalar(distance));
    camera.far = Math.max(camera.far, distance * 10);
    controls.update();
    controls.saveState();
    controls.listenToKeyEvents(canvas);

    const render = () => renderer.render(scene, camera);
    const resize = () => {
        const w = Math.max(canvas.clientWidth, 1);
        const h = Math.max(canvas.clientHeight, 1);
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        render();
    };

    controls.addEventListener('change', render);
    canvas.addEventListener('keydown', (event) => {
        if (event.key === '+' || event.key === '=' || event.key === '-') {
            const factor = event.key === '-' ? 1.2 : 1 / 1.2;
            const offset = camera.position.clone().sub(controls.target).multiplyScalar(factor);
            if (offset.length() >= controls.minDistance && offset.length() <= controls.maxDistance) {
                camera.position.copy(controls.target).add(offset);
                controls.update();
            }

            event.preventDefault();
        } else if (event.key === 'Home') {
            controls.reset();
            event.preventDefault();
        }
    });
    canvas.addEventListener('webglcontextlost', () => notify('error'));

    const observer = new ResizeObserver(resize);
    observer.observe(canvas);
    resize();
    requestAnimationFrame(() => {
        render();
        notify('loaded');
    });
}

showModel().catch(() => notify('error'));
