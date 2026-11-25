namespace Jarvis.Automation.Browser;

/// <summary>JavaScript injected into pages to build a compact, LLM-friendly snapshot with element refs.</summary>
internal static class PageScripts
{
    public const string RefAttribute = "data-jarvis-ref";

    /// <summary>Argument: { limit, textBudget }. Returns { url, title, scrollY, scrollHeight, viewportHeight, lines[] }.</summary>
    public const string Snapshot = """
        ({ limit, textBudget }) => {
          const REF = 'data-jarvis-ref';
          // Refs persist across snapshots of the same document, so unchanged elements keep their ids.
          let next = window.__jarvisNext || 0;

          const interactiveRoles = new Set(['button','link','checkbox','radio','tab','menuitem','menuitemcheckbox','menuitemradio',
            'option','switch','combobox','textbox','searchbox','slider','spinbutton','treeitem','listbox']);
          const textTags = new Set(['H1','H2','H3','H4','H5','H6','P','LI','TD','TH','LABEL','BLOCKQUOTE','PRE','FIGCAPTION','DT','DD']);
          const lines = [];
          let refCount = 0, textUsed = 0, below = 0, above = 0;
          const vh = window.innerHeight, vw = window.innerWidth;

          const clean = s => (s || '').replace(/\s+/g, ' ').trim();
          const cut = (s, n) => s.length > n ? s.slice(0, n) + '…' : s;
          const shortHref = href => {
            if (!href || href.startsWith('javascript:') || href === '#') return '';
            try {
              const u = new URL(href, location.href);
              return cut(u.origin === location.origin ? u.pathname + u.search : u.host + u.pathname, 60);
            } catch { return cut(href, 60); }
          };

          const isVisible = el => {
            if (el.getAttribute('aria-hidden') === 'true') return false;
            const r = el.getBoundingClientRect();
            if (r.width < 1 || r.height < 1) return false;
            const st = getComputedStyle(el);
            return st.visibility !== 'hidden' && st.display !== 'none' && st.opacity !== '0';
          };

          const roleOf = el => {
            const role = el.getAttribute('role');
            if (role) return role;
            const tag = el.tagName;
            if (tag === 'A' && el.hasAttribute('href')) return 'link';
            if (tag === 'BUTTON' || tag === 'SUMMARY') return 'button';
            if (tag === 'SELECT') return 'combobox';
            if (tag === 'TEXTAREA') return 'textbox';
            if (tag === 'INPUT') {
              const t = (el.type || 'text').toLowerCase();
              if (['button','submit','reset','image'].includes(t)) return 'button';
              if (t === 'checkbox' || t === 'radio') return t;
              if (t === 'range') return 'slider';
              if (t === 'hidden') return null;
              return t === 'search' ? 'searchbox' : 'textbox';
            }
            if (el.isContentEditable && (el.parentElement === null || !el.parentElement.isContentEditable)) return 'textbox';
            if (el.hasAttribute('onclick') || (el.tabIndex >= 0 && el.hasAttribute('tabindex'))) return 'button';
            return null;
          };

          const nameOf = el => {
            const label = el.getAttribute('aria-label');
            if (label) return clean(label);
            const by = el.getAttribute('aria-labelledby');
            if (by) {
              const t = by.split(' ').map(id => document.getElementById(id)?.innerText || '').join(' ');
              if (clean(t)) return clean(t);
            }
            if (el.labels && el.labels.length) return clean(el.labels[0].innerText);
            if (el.tagName === 'INPUT' && ['button','submit','reset'].includes(el.type)) return clean(el.value);
            const text = clean(el.innerText || el.textContent);
            if (text) return text;
            return clean(el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('alt') ||
              el.querySelector?.('img[alt]')?.getAttribute('alt') || el.getAttribute('name') || '');
          };

          const walk = root => {
            const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
            let el = walker.currentNode.nodeType === 1 ? walker.currentNode : walker.nextNode();
            while (el) {
              if (refCount >= limit) return;
              if (['SCRIPT','STYLE','NOSCRIPT','TEMPLATE','SVG'].includes(el.tagName.toUpperCase())) { el = walker.nextSibling() || nextOutside(walker); continue; }
              const role = roleOf(el);
              const rect = el.getBoundingClientRect();
              const far = rect.top > vh * 2 || rect.bottom < -vh;
              if (role && interactiveRoles.has(role) && isVisible(el) && !el.parentElement?.closest('[' + REF + ']:not([contenteditable])')) {
                if (far) { if (rect.top > 0) below++; else above++; el = walker.nextNode(); continue; }
                refCount++;
                const ref = el.getAttribute(REF) || ('e' + (++next));
                el.setAttribute(REF, ref);
                const r = rect;
                const name = cut(nameOf(el), 80);
                let line = '[' + ref + '] ' + role + ' "' + name + '"';
                // Link targets cost many tokens and are rarely needed: show them only for unlabeled links.
                if (role === 'link' && !name) {
                  const href = shortHref(el.getAttribute('href') || '');
                  if (href) line += ' -> ' + href;
                }
                if ('value' in el && el.tagName !== 'BUTTON' && role !== 'button' && el.value) {
                  line += el.type === 'password' ? ' value=<hidden>' : ' value="' + cut(clean(String(el.value)), 80) + '"';
                }
                if (el.type === 'password') line += ' (password)';
                if (el.checked) line += ' (checked)';
                if (el.disabled || el.getAttribute('aria-disabled') === 'true') line += ' (disabled)';
                if (el.tagName === 'SELECT') line += ' options: ' + cut([...el.options].map(o => o.text).join(' | '), 150);
                if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) line += ' (offscreen)';
                lines.push(line);
              } else if (!far && textTags.has(el.tagName) && textUsed < textBudget && isVisible(el) && !el.querySelector('p,li,td,h1,h2,h3,h4')) {
                const text = cut(clean(el.innerText), 300);
                // Skip blocks that only repeat the labels of the links/buttons inside them (menus, nav lists).
                const ownText = [...el.querySelectorAll('a,button')].reduce((n, c) => n - clean(c.innerText).length, text.length);
                if (text && ownText > 15) {
                  const prefix = /^H\d$/.test(el.tagName) ? 'heading: ' : 'text: ';
                  lines.push(prefix + text);
                  textUsed += text.length;
                }
              }
              if (el.shadowRoot) walk(el.shadowRoot);
              el = walker.nextNode();
            }
          };
          const nextOutside = w => { while (w.parentNode()) { const s = w.nextSibling(); if (s) return s; } return null; };

          walk(document.body || document.documentElement);
          window.__jarvisNext = next;
          if (above > 0) lines.unshift('…' + above + ' more elements above (scroll up to see them)');
          if (below > 0) lines.push('…' + below + ' more elements below (scroll down to see them)');
          return {
            url: location.href, title: document.title,
            scrollY: Math.round(window.scrollY), scrollHeight: document.documentElement.scrollHeight, viewportHeight: vh,
            truncated: refCount >= limit, lines
          };
        }
        """;

    public const string Viewport = "() => ({ innerWidth: window.innerWidth, innerHeight: window.innerHeight, devicePixelRatio: window.devicePixelRatio })";

    /// <summary>Argument: maxLength. Returns the readable text of the page body.</summary>
    public const string ReadText = "(max) => { const t = (document.body?.innerText || '').replace(/\\n{3,}/g, '\\n\\n'); return t.length > max ? t.slice(0, max) + '\\n…(truncated)' : t; }";
}
