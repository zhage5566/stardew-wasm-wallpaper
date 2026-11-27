'use strict';

const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { Readable } = require('node:stream');

const gameUrl = 'stardew://desktop/index.html';
const runtimePart = '_framework/dotnet.native.0zuq1uewq8.wasm0';
const runtimeHash = '548790469707875f1eeb6396d22b191eb4ef87d18430a63c02c976586f9b22dd';
const mime = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json', '.wasm': 'application/wasm', '.png': 'image/png',
  '.svg': 'image/svg+xml', '.count': 'text/plain; charset=utf-8'
};

function isGameFolder(folder) {
  return typeof folder === 'string' && [
    'index.html', 'Content.tar.count', '_framework/dotnet.js', runtimePart
  ].every(file => fs.existsSync(path.join(folder, file)));
}

function replace(source, from, to) {
  if (!source.includes(from)) throw new Error('游戏文件与支持的版本不符');
  return source.replace(from, to);
}

function patchRuntime(bytes) {
  if (createHash('sha256').update(bytes).digest('hex') !== runtimeHash) {
    throw new Error('游戏运行时校验失败。请使用发行包配套的 game 文件夹。');
  }
  // The same cursor-only AOT changes as the Windows host.
  for (const [offset, size, result] of [[6517142, 152, [0x41, 1]], [11820752, 702, []]]) {
    bytes.fill(1, offset, offset + size);
    bytes[offset] = 0;
    bytes.set(result, offset + 1);
    bytes[offset + size - 1] = 0x0b;
  }
  for (const offset of [11448038, 11758844, 12235988]) bytes.set([0x1a, 0x41, 1, 1], offset);
  return bytes;
}

function patchHtml(html) {
  const script = `<style>html,body,canvas{cursor:default!important} #music-choice{display:none!important}</style>
<script>
addEventListener('DOMContentLoaded', () => {
  const loading = document.getElementById('loading');
  const start = document.getElementById('btn-with-music');
  const timer = setInterval(() => {
    if (loading.classList.contains('hidden')) clearInterval(timer);
    else if (typeof start.onclick === 'function') { clearInterval(timer); start.click(); }
  }, 100);
});
</script>`;
  return replace(html, '<script type="module" src="game.js"></script>',
    script + '<script type="module" src="game.js"></script>');
}

function patchResize(script) {
  script = replace(script, 'new ResizeObserver(() => {',
    'let pixelWidth = 0, pixelHeight = 0;\nconst resizeGame = () => {');
  script = replace(script,
    'if (nw > 0 && nh > 0) try { exports.WasmBootstrap.Resize(nw, nh); } catch {}',
    'if (nw > 0 && nh > 0 && (nw !== pixelWidth || nh !== pixelHeight)) try {' +
    ' exports.WasmBootstrap.Resize(nw, nh); pixelWidth = nw; pixelHeight = nh; } catch {}');
  return replace(script, '}).observe(canvas);',
    `};
new ResizeObserver(resizeGame).observe(canvas);
addEventListener('resize', resizeGame);
let lastDpr = devicePixelRatio;
setInterval(() => {
  if (lastDpr !== devicePixelRatio) { lastDpr = devicePixelRatio; resizeGame(); }
}, 250);`);
}

function install(session, folder) {
  const root = fs.realpathSync(folder);
  session.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] },
    (_request, done) => done({ cancel: true }));
  session.protocol.handle('stardew', async request => {
    try {
      const url = new URL(request.url);
      if (url.host !== 'desktop' || request.method !== 'GET') return new Response(null, { status: 404 });
      const relative = decodeURIComponent(url.pathname.slice(1)) || 'index.html';
      const file = await fsp.realpath(path.resolve(root, relative));
      const contained = path.relative(root, file);
      if (!contained || contained === '..' || contained.startsWith('..' + path.sep) || path.isAbsolute(contained)) {
        return new Response(null, { status: 404 });
      }
      const headers = {
        'Content-Type': mime[path.extname(file)] || 'application/octet-stream',
        'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp',
        'Cross-Origin-Resource-Policy': 'same-origin', 'X-Content-Type-Options': 'nosniff',
        'Cache-Control': 'no-store'
      };
      let content;
      if (relative === runtimePart) content = patchRuntime(await fsp.readFile(file));
      else if (relative === 'index.html') content = patchHtml(await fsp.readFile(file, 'utf8'));
      else if (relative === 'game.js') content = patchResize(await fsp.readFile(file, 'utf8'));
      else if (relative === '_framework/blazor.boot.json') {
        const config = JSON.parse(await fsp.readFile(file, 'utf8'));
        config.cacheBootResources = false;
        content = JSON.stringify(config);
      } else {
        content = Readable.toWeb(fs.createReadStream(file));
      }
      return new Response(content, { headers });
    } catch (error) {
      if (error.code === 'ENOENT') return new Response(null, { status: 404 });
      console.error(error);
      return new Response('读取游戏文件失败', { status: 500 });
    }
  });
}

module.exports = { gameUrl, isGameFolder, install };
