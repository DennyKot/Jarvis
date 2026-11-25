// Jarvis Bridge: connects to the local Jarvis app and executes its browser commands.
importScripts('config.js');

let socket = null;
let currentTabId = null;
let listedTabIds = [];

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  try {
    socket = new WebSocket(`ws://localhost:${JARVIS.port}/?token=${JARVIS.token}`);
  } catch {
    socket = null;
    return;
  }
  socket.onmessage = async (event) => {
    const message = JSON.parse(event.data);
    if (message.id === undefined) return;
    try {
      const result = await handle(message.cmd, message.args || {});
      send({ id: message.id, ok: true, result: result === undefined ? null : result });
    } catch (error) {
      send({ id: message.id, ok: false, error: String((error && error.message) || error) });
    }
  };
  socket.onclose = () => { socket = null; };
  socket.onerror = () => {};
}

function send(payload) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(payload));
}

// Service workers are suspended when idle; alarms wake them up to reconnect, and the ping keeps an open socket alive.
chrome.alarms.create('jarvis-reconnect', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(connect);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
setInterval(connect, 3000);
setInterval(() => send({ ping: 1 }), 20000);
connect();

chrome.tabs.onCreated.addListener((tab) => {
  if (tab.openerTabId !== undefined && tab.openerTabId === currentTabId && tab.active) currentTabId = tab.id;
});
chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === currentTabId) currentTabId = null;
});

async function currentTab() {
  if (currentTabId !== null) {
    try { return await chrome.tabs.get(currentTabId); } catch { currentTabId = null; }
  }
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab) throw new Error('No browser tab is open.');
  currentTabId = tab.id;
  return tab;
}

function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

function waitLoaded(tabId, timeout = 15000) {
  return new Promise((resolve) => {
    const done = () => {
      chrome.tabs.onUpdated.removeListener(listener);
      clearTimeout(timer);
      resolve();
    };
    const listener = (id, info) => { if (id === tabId && info.status === 'complete') done(); };
    const timer = setTimeout(done, timeout);
    chrome.tabs.onUpdated.addListener(listener);
  });
}

// After a click or key press the page may start navigating; wait for it only if it does.
async function settle(tabId) {
  await sleep(250);
  try {
    const tab = await chrome.tabs.get(currentTabId ?? tabId);
    if (tab.status === 'loading') await waitLoaded(tab.id, 10000);
  } catch { /* tab closed */ }
  await sleep(100);
}

async function exec(tabId, func, args = []) {
  const [frame] = await chrome.scripting.executeScript({ target: { tabId }, func, args });
  return frame ? frame.result : null;
}

async function injectPageScripts(tabId) {
  await chrome.scripting.executeScript({ target: { tabId }, files: ['page-scripts.js'] });
}

async function focusTab(tab) {
  await chrome.tabs.update(tab.id, { active: true });
  await chrome.windows.update(tab.windowId, { focused: true });
}

async function handle(cmd, a) {
  switch (cmd) {
    case 'hello':
      return { version: chrome.runtime.getManifest().version };

    case 'open': {
      let tab;
      if (a.newTab) {
        tab = await chrome.tabs.create({ url: a.url, active: true });
      } else {
        const current = await currentTab();
        tab = await chrome.tabs.update(current.id, { url: a.url, active: true });
      }
      currentTabId = tab.id;
      await chrome.windows.update(tab.windowId, { focused: true });
      await waitLoaded(tab.id);
      return null;
    }

    case 'snapshot': {
      const tab = await currentTab();
      await injectPageScripts(tab.id);
      const result = await exec(tab.id, (options) => window.__jarvis.snapshot(options), [a]);
      return { ...result, tabId: tab.id };
    }

    case 'readText': {
      const tab = await currentTab();
      await injectPageScripts(tab.id);
      return await exec(tab.id, (ref, max) => {
        if (!ref) return window.__jarvis.readText(max);
        const el = document.querySelector(`[data-jarvis-ref="${ref}"]`);
        if (!el) return null;
        const text = el.innerText || '';
        return text.length > max ? text.slice(0, max) + '\n…(truncated)' : text;
      }, [a.ref || null, a.max || 4000]);
    }

    case 'rect': {
      const tab = await currentTab();
      const result = await exec(tab.id, (ref) => {
        const el = document.querySelector(`[data-jarvis-ref="${ref}"]`);
        if (!el) return { missing: true };
        el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        const r = el.getBoundingClientRect();
        const name = (el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('title') || '').trim().slice(0, 120);
        return {
          x: r.left, y: r.top, width: r.width, height: r.height, name, type: el.type || '',
          innerWidth: window.innerWidth, innerHeight: window.innerHeight, devicePixelRatio: window.devicePixelRatio,
        };
      }, [a.ref]);
      return { ...result, active: tab.active, title: tab.title || '' };
    }

    case 'click': {
      const tab = await currentTab();
      const result = await exec(tab.id, (ref, double) => {
        const el = document.querySelector(`[data-jarvis-ref="${ref}"]`);
        if (!el) return { missing: true };
        el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        const r = el.getBoundingClientRect();
        const init = { bubbles: true, cancelable: true, composed: true, view: window, button: 0,
          clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
        el.dispatchEvent(new PointerEvent('pointerdown', { ...init, pointerType: 'mouse', isPrimary: true }));
        el.dispatchEvent(new MouseEvent('mousedown', init));
        el.dispatchEvent(new PointerEvent('pointerup', { ...init, pointerType: 'mouse', isPrimary: true }));
        el.dispatchEvent(new MouseEvent('mouseup', init));
        if (typeof el.focus === 'function') el.focus();
        el.click();
        if (double) el.dispatchEvent(new MouseEvent('dblclick', init));
        return { ok: true };
      }, [a.ref, !!a.doubleClick]);
      await settle(tab.id);
      return result;
    }

    case 'type': {
      const tab = await currentTab();
      const result = await exec(tab.id, (ref, text, clear, submit) => {
        const el = document.querySelector(`[data-jarvis-ref="${ref}"]`);
        if (!el) return { missing: true };
        el.scrollIntoView({ block: 'nearest' });
        el.focus();
        if (el.isContentEditable) {
          if (clear) document.execCommand('selectAll');
          document.execCommand('insertText', false, text);
        } else {
          // The native setter keeps frameworks such as React in sync with the new value.
          const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, (clear ? '' : el.value) + text);
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (submit) {
          const key = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
          const notHandled = el.dispatchEvent(new KeyboardEvent('keydown', key));
          el.dispatchEvent(new KeyboardEvent('keypress', key));
          el.dispatchEvent(new KeyboardEvent('keyup', key));
          if (notHandled && el.form) {
            if (el.form.requestSubmit) el.form.requestSubmit(); else el.form.submit();
          }
        }
        return { ok: true };
      }, [a.ref, a.text || '', a.clear !== false, !!a.submit]);
      await settle(tab.id);
      return result;
    }

    case 'select': {
      const tab = await currentTab();
      const result = await exec(tab.id, (ref, value) => {
        const el = document.querySelector(`[data-jarvis-ref="${ref}"]`);
        if (!el) return { missing: true };
        const option = [...el.options].find((o) => o.text.trim() === value || o.value === value)
          || [...el.options].find((o) => o.text.toLowerCase().includes(String(value).toLowerCase()));
        if (!option) return { error: `No option "${value}".` };
        el.value = option.value;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { ok: true };
      }, [a.ref, a.value]);
      await settle(tab.id);
      return result;
    }

    case 'press': {
      const tab = await currentTab();
      await exec(tab.id, (keys) => {
        const parts = keys.split('+');
        const key = parts.pop();
        const target = document.activeElement || document.body;
        const init = {
          key, code: key.length === 1 ? `Key${key.toUpperCase()}` : key, bubbles: true, cancelable: true,
          ctrlKey: parts.includes('Control'), shiftKey: parts.includes('Shift'), altKey: parts.includes('Alt'), metaKey: parts.includes('Meta'),
          keyCode: key === 'Enter' ? 13 : key === 'Escape' ? 27 : key === 'Tab' ? 9 : 0,
        };
        const notHandled = target.dispatchEvent(new KeyboardEvent('keydown', init));
        target.dispatchEvent(new KeyboardEvent('keyup', init));
        if (key === 'Enter' && notHandled && target.form) target.form.requestSubmit();
        if (key === 'PageDown') window.scrollBy(0, window.innerHeight * 0.85);
        if (key === 'PageUp') window.scrollBy(0, -window.innerHeight * 0.85);
      }, [a.keys]);
      await settle(tab.id);
      return null;
    }

    case 'scroll': {
      const tab = await currentTab();
      await exec(tab.id, (dy) => window.scrollBy({ top: dy }), [a.dy]);
      await sleep(200);
      return null;
    }

    case 'back': {
      const tab = await currentTab();
      await chrome.tabs.goBack(tab.id);
      await waitLoaded(tab.id, 10000);
      return null;
    }

    case 'tabs': {
      const tabs = await chrome.tabs.query({});
      if (a.action === 'switch' || a.action === 'close') {
        const id = listedTabIds[a.index];
        if (id === undefined) throw new Error('Unknown tab index; list the tabs first.');
        if (a.action === 'close') {
          await chrome.tabs.remove(id);
          return null;
        }
        const tab = await chrome.tabs.get(id);
        await focusTab(tab);
        currentTabId = id;
        return null;
      }
      const current = await currentTab();
      listedTabIds = tabs.map((t) => t.id);
      return tabs.map((t, index) => ({ index, title: t.title || '', url: t.url || '', current: t.id === current.id }));
    }

    default:
      throw new Error(`Unknown command '${cmd}'.`);
  }
}
