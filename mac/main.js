'use strict';

const { app, BrowserWindow, Menu, Tray, dialog, globalShortcut, nativeImage, protocol, screen, session } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { execFileSync } = require('node:child_process');
const { gameUrl, isGameFolder, install } = require('./game-host');

protocol.registerSchemesAsPrivileged([{
  scheme: 'stardew',
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true }
}]);

const wrapper = process.env.STARDEW_APP_BUNDLE;
const data = path.join(app.getPath('appData'), 'Stardew Wallpaper');
fs.mkdirSync(data, { recursive: true });
app.setPath('userData', data);
const settingsPath = path.join(data, 'wallpaper.json');
const loginItem = path.join(os.homedir(), 'Library', 'LaunchAgents', 'app.zhouyang.stardew-wallpaper.plist');
let settings = {};
try { settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8')); }
catch (error) { if (error.code !== 'ENOENT') console.error(error); }

let win, tray;
let playing = false;
let selectedDisplay = settings.favorite ? settings.display : null;
const playShortcut = 'Command+Alt+F10';
const desktopLevel = getDesktopLevel();

function getDesktopLevel() {
  if (process.platform !== 'darwin') return 0;
  try {
    const level = Number(execFileSync('/usr/bin/osascript',
      ['-l', 'JavaScript', '-e', 'ObjC.import("CoreGraphics"); $.CGWindowLevelForKey(2)'],
      { encoding: 'utf8', timeout: 3000 }).trim());
    if (Number.isInteger(level) && level < 0 && level > -2147483648) return level + 1;
  } catch (error) { console.error(error.message); }
  return -2147483622;
}

function preserveSettings() {
  const temporary = settingsPath + '.tmp';
  fs.writeFileSync(temporary, JSON.stringify(settings));
  fs.renameSync(temporary, settingsPath);
}

function startupEnabled() {
  if (!wrapper) return false;
  try { return fs.readFileSync(loginItem, 'utf8').includes('<string>' + xml(wrapper) + '</string>'); }
  catch { return false; }
}

function xml(value) {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function toggleStartup() {
  if (startupEnabled()) fs.rmSync(loginItem, { force: true });
  else {
    fs.mkdirSync(path.dirname(loginItem), { recursive: true });
    const plist = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>app.zhouyang.stardew-wallpaper</string>
<key>RunAtLoad</key><true/>
<key>ProgramArguments</key><array><string>/usr/bin/open</string><string>-g</string><string>${xml(wrapper)}</string></array>
</dict></plist>`;
    const temporary = loginItem + '.tmp';
    fs.writeFileSync(temporary, plist, { mode: 0o644 });
    fs.renameSync(temporary, loginItem);
  }
}

function updateGeometry() {
  if (!win || win.isDestroyed()) return;
  const target = screen.getAllDisplays().find(display => display.id === selectedDisplay) || screen.getPrimaryDisplay();
  win.setBounds(target.bounds);
}

function setPlaying(enable) {
  if (!win || win.isDestroyed()) return;
  playing = enable;
  win.setIgnoreMouseEvents(!enable);
  if (enable) {
    win.setAlwaysOnTop(true, 'pop-up-menu');
    win.show();
    app.focus({ steal: true });
    win.focus();
    win.webContents.focus();
  } else {
    win.setAlwaysOnTop(true, 'normal', desktopLevel);
    win.showInactive();
    win.blur();
  }
  refreshTray();
}

function refreshTray() {
  if (!tray) return;
  const displayMenu = [{ label: '自动跟随主显示器', type: 'radio', checked: selectedDisplay == null,
    click: () => selectDisplay(null) }];
  for (const display of screen.getAllDisplays()) {
    displayMenu.push({ label: display.label || `${display.size.width}×${display.size.height}`,
      type: 'radio', checked: display.id === selectedDisplay, click: () => selectDisplay(display.id) });
  }
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: playing ? '返回桌面壁纸' : '全屏试玩', click: () => setPlaying(!playing) },
    { label: '选择显示器', submenu: displayMenu },
    { label: '设为常用壁纸', type: 'checkbox', checked: Boolean(settings.favorite), click: () => changed(() => {
      settings.favorite = !settings.favorite;
      settings.display = settings.favorite ? selectedDisplay : null;
      preserveSettings();
    }) },
    { label: '登录时自动启动', type: 'checkbox', checked: startupEnabled(), enabled: Boolean(wrapper),
      click: () => changed(toggleStartup) },
    { type: 'separator' },
    { label: '关于 · zhouyang', click: () => dialog.showMessageBox({ title: '星露谷壁纸',
      message: '桌面版制作 zhouyang', detail: '《星露谷物语》原作 ConcernedApe' }) },
    { label: '退出', click: () => app.quit() }
  ]));
}

function changed(change) {
  try { change(); }
  catch (error) { dialog.showErrorBox('无法保存设置', error.message); }
  refreshTray();
}

function selectDisplay(id) {
  selectedDisplay = id;
  if (settings.favorite) changed(() => {
    settings.display = id;
    preserveSettings();
  });
  else refreshTray();
  updateGeometry();
}

async function chooseGameFolder() {
  const candidates = [process.env.STARDEW_GAME_DIR, settings.gameFolder];
  if (wrapper) candidates.push(path.join(path.dirname(wrapper), 'game'));
  const found = candidates.find(isGameFolder);
  if (found) return found;
  const selected = await dialog.showOpenDialog({
    title: '选择发行包中的 game 文件夹', message: '选择已解压的 Windows 发行包里的 game 文件夹',
    properties: ['openDirectory']
  });
  if (selected.canceled) return null;
  const folder = selected.filePaths[0];
  if (!isGameFolder(folder)) {
    dialog.showErrorBox('缺少游戏文件', '请完整解压配套的 Windows ZIP，再选择其中的 game 文件夹。');
    return null;
  }
  settings.gameFolder = folder;
  preserveSettings();
  return folder;
}

async function start() {
  const folder = await chooseGameFolder();
  if (!folder) { app.quit(); return; }
  if (app.dock) app.dock.hide();
  const gameSession = session.fromPartition('persist:stardew-wallpaper');
  gameSession.setPermissionRequestHandler((_contents, _permission, done) => done(false));
  install(gameSession, folder);
  win = new BrowserWindow({
    frame: false, show: false, movable: false, resizable: false, fullscreenable: false,
    skipTaskbar: true, hasShadow: false, backgroundColor: '#000000',
    webPreferences: { session: gameSession, sandbox: true, contextIsolation: true, nodeIntegration: false,
      backgroundThrottling: false }
  });
  win.setHiddenInMissionControl(true);
  win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: false, skipTransformProcessType: true });
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('will-navigate', (event, url) => { if (url !== gameUrl) event.preventDefault(); });
  win.on('blur', () => { if (playing) setPlaying(false); });
  const icon = nativeImage.createFromPath(path.join(__dirname, 'menubar.png'));
  icon.setTemplateImage(true);
  tray = new Tray(icon);
  tray.setToolTip('星露谷壁纸 · zhouyang');
  refreshTray();
  globalShortcut.register(playShortcut, () => setPlaying(!playing));
  for (const event of ['display-added', 'display-removed', 'display-metrics-changed']) {
    screen.on(event, () => { updateGeometry(); refreshTray(); });
  }
  updateGeometry();
  setPlaying(false);
  await win.loadURL(gameUrl);
}

if (!app.requestSingleInstanceLock()) app.quit();
else app.whenReady().then(start).catch(error => {
  dialog.showErrorBox('星露谷壁纸启动失败', error.message);
  app.quit();
});
app.on('will-quit', () => globalShortcut.unregisterAll());
