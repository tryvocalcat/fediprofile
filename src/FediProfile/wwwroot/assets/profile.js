// FediProfile - Profile page functionality

// Derive the user base path from the current URL (e.g. "/john" -> "/john")
const basePath = window.location.pathname.replace(/\/$/, '');

async function loadProfile() {
  try {
    const url = basePath + '?t=' + Date.now();
    const response = await fetch(url, {
      headers: { 'Accept': 'application/activity+json' }
    });

    if (!response.ok) throw new Error('Failed to fetch profile');
    
    const actor = await response.json();

    // Apply theme from _fediprofile extension
    applyTheme(actor._fediprofile?.theme);

    // Populate name and bio
    const nameEl = document.getElementById('profile-name');
    const bioEl = document.getElementById('profile-bio');
    
    if (nameEl) nameEl.textContent = actor.name || 'Profile';
    if (bioEl) bioEl.textContent = actor.summary || '';

    // Populate avatar
    const avatarEl = document.getElementById('profile-avatar');
    const iconUrl = actor.icon?.url;
    if (avatarEl && iconUrl) {
      avatarEl.src = iconUrl;
      avatarEl.alt = (actor.name || 'Profile') + ' avatar';
      avatarEl.style.display = 'block';
    }

    // Update page title
    if (actor.name) document.title = actor.name + ' - FediProfile';

    // Render links from attachments
    renderLinks(actor.attachment || []);

    // Fetch and render badges
    await loadBadges();

    // Fetch and render recent posts
    await loadRecentPosts();
  } catch (error) {
    console.error(error);
    document.getElementById('profile-container').innerHTML = '<p>Error loading profile.</p>';
  }
}


function renderLinks(attachments) {
  const container = document.getElementById('profile-container');

  if (!attachments || attachments.length === 0) {
    container.innerHTML = '<p>No links yet.</p>';
    return;
  }

  let html = '<div class="links-container">';
  const categories = {};

  // Group by category
  attachments.forEach(link => {
    if (link == null || typeof link !== 'object') return;
    const cat = link.category || '';
    if (!categories[cat]) {
      categories[cat] = [];
    }
    categories[cat].push(link);
  });

  // Render by category — uncategorized first, then social, then rest alphanumerically
  const sortedEntries = Object.entries(categories).sort(([a], [b]) => {
    if (a === '') return -1;
    if (b === '') return 1;
    if (a.toLowerCase() === 'social' && b.toLowerCase() !== 'social') return -1;
    if (b.toLowerCase() === 'social' && a.toLowerCase() !== 'social') return 1;
    return a.localeCompare(b, undefined, { numeric: true });
  });

  // Generate tab bar for themes that support tabbed categories (hidden by default via CSS)
  const hasNamedCategories = sortedEntries.some(([cat]) => cat !== '');
  if (hasNamedCategories && sortedEntries.length > 1) {
    html += '<div class="category-tabs" role="tablist">';
    sortedEntries.forEach(([cat], idx) => {
      const label = cat || 'Links';
      html += `<button class="category-tab${idx === 0 ? ' active' : ''}" role="tab" aria-selected="${idx === 0}" data-tab="${idx}">${escapeHtml(label)}</button>`;
    });
    html += '</div>';
  }

  sortedEntries.forEach(([cat, catLinks], idx) => {
    const activeClass = idx === 0 ? ' active' : '';
    html += cat
      ? `<div class="category${activeClass}" data-tab-panel="${idx}"><h2>${escapeHtml(cat)}</h2>`
      : `<div class="category${activeClass}" data-tab-panel="${idx}">`;
    catLinks.forEach(link => {
      const url = link.href || '#';
      const iconUrl = link.icon?.url;
      html += `
        <a href="${escapeHtml(url)}" class="link-card" rel="me" data-type="${escapeHtml(link.type || 'link')}">
          ${iconUrl ? `<img src="${escapeHtml(iconUrl)}" alt="" class="link-icon" />` : ''}
          <div class="link-content">
            <strong>${escapeHtml(link.name)}${link.verified ? '<span class="verified-badge" title="Verified"><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12l2 2 4-4"/></svg></span>' : ''}</strong>
            ${link.description ? `<p>${escapeHtml(link.description)}</p>` : ''}
          </div>
          ${link.autoBoost ? '<span class="boost-badge" title="Boosted"><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 1l4 4-4 4"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><path d="M7 23l-4-4 4-4"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/></svg></span>' : ''}
        </a>
      `;
    });
    html += '</div>';
  });

  html += '</div>';
  container.innerHTML = html;

  // Initialize tab switching for themes that display the tab bar
  initCategoryTabs();
}

// Tab switching for category tabs (used by themes with tabbed layout like Cosmos)
function initCategoryTabs() {
  const tabBar = document.querySelector('.category-tabs');
  if (!tabBar) return;

  tabBar.querySelectorAll('.category-tab').forEach(tab => {
    tab.addEventListener('click', () => {
      const tabId = tab.dataset.tab;
      // Deactivate all tabs and panels
      tabBar.querySelectorAll('.category-tab').forEach(t => {
        t.classList.remove('active');
        t.setAttribute('aria-selected', 'false');
      });
      document.querySelectorAll('.category[data-tab-panel]').forEach(p => p.classList.remove('active'));
      // Activate selected tab and panel
      tab.classList.add('active');
      tab.setAttribute('aria-selected', 'true');
      const panel = document.querySelector(`.category[data-tab-panel="${tabId}"]`);
      if (panel) panel.classList.add('active');
    });
  });
}

async function loadBadges() {
  try {
    const url = basePath + '/badges?t=' + Date.now();
    const response = await fetch(url);
    if (!response.ok) return;

    const badges = await response.json();
    if (!badges || badges.length === 0) return;

    const section = document.getElementById('badges-section');
    const container = document.getElementById('badges-container');
    if (!section || !container) return;

    let html = '';
    badges.forEach(badge => {
      const title = escapeHtml(badge.title || 'Badge');
      const image = badge.image;
      const description = badge.description ? escapeHtml(badge.description) : '';
      const issuedOn = badge.issuedOn ? escapeHtml(badge.issuedOn) : '';
      const noteId = badge.noteId || '';

      const imgHtml = image
        ? `<img src="${escapeHtml(image)}" alt="${title}" class="badge-image" />`
        : `<div class="badge-image badge-placeholder">\uD83C\uDFC5</div>`;

      const linkOpen = noteId ? `<a href="${escapeHtml(noteId)}" target="_blank" rel="noopener" class="badge-card">` : '<div class="badge-card">';
      const linkClose = noteId ? '</a>' : '</div>';

      html += `
        ${linkOpen}
          ${imgHtml}
          <div class="badge-info">
            <strong class="badge-title">${title}</strong>
            ${issuedOn ? `<span class="badge-date">${issuedOn}</span>` : ''}
          </div>
        ${linkClose}
      `;
    });

    container.innerHTML = html;
    section.style.display = '';
  } catch (err) {
    // Badges are optional — silently ignore errors
  }
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

function stripHtml(html) {
  const tmp = document.createElement('div');
  tmp.innerHTML = html;
  return tmp.textContent || tmp.innerText || '';
}

function truncateText(text, maxLen) {
  if (text.length <= maxLen) return text;
  return text.substring(0, maxLen) + '...';
}

function getDomain(url) {
  try { return new URL(url).hostname; } catch { return ''; }
}

function faviconUrl(postUrl) {
  const domain = getDomain(postUrl);
  return domain ? `https://www.google.com/s2/favicons?domain=${encodeURIComponent(domain)}&sz=32` : '';
}

async function loadRecentPosts() {
  try {
    const url = basePath + '/recent-posts?t=' + Date.now();
    const response = await fetch(url);
    if (!response.ok) return;

    const posts = await response.json();
    if (!posts || posts.length === 0) return;

    const section = document.getElementById('recent-posts-section');
    const container = document.getElementById('recent-posts-container');
    if (!section || !container) return;

    let html = '';
    posts.forEach(post => {
      const rawContent = post.content || '';
      const postUrl = post.url || '#';
      const date = formatRelativeDate(post.boostedUtc || post.publishedUtc);

      // Clean HTML and truncate to 300 chars
      let cleanText = stripHtml(rawContent).trim();
      if (!cleanText) {
        const domain = getDomain(postUrl);
        cleanText = domain ? 'Recent post on ' + domain : 'Recent post';
      }
      const displayText = truncateText(cleanText, 300);

      // Favicon from the post URL domain
      const favicon = faviconUrl(postUrl);
      const faviconHtml = favicon ? `<img src="${escapeHtml(favicon)}" alt="" class="rp-favicon" width="16" height="16" />` : '';

      const summary = post.summary
        ? `<details class="rp-cw"><summary>${escapeHtml(post.summary)}</summary><div class="rp-content">${escapeHtml(displayText)}</div></details>`
        : `<div class="rp-content">${escapeHtml(displayText)}</div>`;

      html += `
        <a href="${escapeHtml(postUrl)}" target="_blank" rel="noopener" class="rp-card">
          <div class="rp-header">
            ${faviconHtml}
            <span class="rp-date">${escapeHtml(date)}</span>
          </div>
          ${summary}
        </a>
      `;
    });

    container.innerHTML = html;
    section.style.display = '';
  } catch (err) {
    // Recent posts are optional — silently ignore errors
  }
}

function formatRelativeDate(isoString) {
  if (!isoString) return '';
  try {
    const date = new Date(isoString.endsWith('Z') ? isoString : isoString + 'Z');
    const now = new Date();
    const diffMs = now - date;
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return diffMins + 'm ago';
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return diffHours + 'h ago';
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 30) return diffDays + 'd ago';
    return date.toLocaleDateString();
  } catch { return ''; }
}

// Apply theme CSS overlay from the actor's _fediprofile.theme setting
function applyTheme(themeName) {
  if (!themeName || themeName === 'theme.css') return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/assets/' + themeName;
  link.id = 'fediprofile-theme-override';
  document.head.appendChild(link);
}

// Populate all fediverse handle elements
function populateFediHandle() {
  const username = window.location.pathname.replace(/^\/|\/$/g, '');
  const domain = window.location.host;
  const address = '@' + username + '@' + domain;
  document.querySelectorAll('[data-fedi-address]').forEach(el => {
    el.textContent = address;
  });
}

// Copy fedi address to clipboard
function initCopyButton() {
  document.querySelectorAll('.copy-fedi-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const address = document.querySelector('[data-fedi-address]')?.textContent;
      if (!address) return;
      navigator.clipboard.writeText(address).then(() => {
        btn.classList.add('copied');
        const svg = btn.querySelector('svg');
        const originalSvg = svg?.outerHTML;
        if (svg) svg.outerHTML = '<svg viewBox="0 0 24 24" width="' + svg.getAttribute('width') + '" height="' + svg.getAttribute('height') + '" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>';
        setTimeout(() => {
          btn.classList.remove('copied');
          const check = btn.querySelector('svg');
          if (check && originalSvg) check.outerHTML = originalSvg;
        }, 1500);
      });
    });
  });
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', () => {
  loadProfile();
  populateFediHandle();
  initCopyButton();
  initShareButton();
  initQrButton();
});

// Share profile via Web Share API or clipboard fallback
function initShareButton() {
  const btn = document.getElementById('share-profile');
  if (!btn) return;
  btn.addEventListener('click', async () => {
    const url = window.location.href;
    const name = document.getElementById('profile-name')?.textContent || 'FediProfile';
    const shareData = {
      title: name,
      text: 'Check out ' + name + "'s profile",
      url: url
    };
    try {
      if (navigator.share) {
        await navigator.share(shareData);
      } else {
        await navigator.clipboard.writeText(url);
        const original = btn.innerHTML;
        btn.innerHTML = '<svg viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"/></svg> Copied!';
        setTimeout(() => { btn.innerHTML = original; }, 2000);
      }
    } catch (err) {
      if (err.name !== 'AbortError') {
        console.error('Share failed:', err);
      }
    }
  });
}

// Show QR code modal for sharing the profile
function initQrButton() {
  const btn = document.getElementById('qr-profile');
  const modal = document.getElementById('qr-modal');
  const closeBtn = document.getElementById('qr-close');
  const downloadBtn = document.getElementById('qr-download');
  if (!btn || !modal) return;

  let generated = false;

  btn.addEventListener('click', () => {
    if (!generated) {
      generateQr();
      generated = true;
    }
    modal.style.display = 'flex';
  });

  // Close on X button
  if (closeBtn) {
    closeBtn.addEventListener('click', () => {
      modal.style.display = 'none';
    });
  }

  // Close on overlay click
  modal.addEventListener('click', (e) => {
    if (e.target === modal) modal.style.display = 'none';
  });

  // Close on Escape
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && modal.style.display !== 'none') {
      modal.style.display = 'none';
    }
  });

  // Download QR code as PNG
  if (downloadBtn) {
    downloadBtn.addEventListener('click', () => {
      const img = document.querySelector('#qr-canvas img');
      if (!img) return;
      const a = document.createElement('a');
      a.href = img.src;
      const username = window.location.pathname.replace(/^\/|\/$/g, '') || 'profile';
      a.download = username + '-qr.png';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
    });
  }
}

function generateQr() {
  const container = document.getElementById('qr-canvas');
  const urlEl = document.getElementById('qr-url');
  const url = window.location.href;

  if (urlEl) urlEl.textContent = url;

  if (typeof qrcode !== 'function') {
    container.textContent = 'QR library not loaded';
    return;
  }

  // Type 0 = auto-detect version, Error correction level L
  const qr = qrcode(0, 'M');
  qr.addData(url);
  qr.make();

  // Render to a data-URI image (cell size 6px, margin 4 cells)
  container.innerHTML = qr.createImgTag(6, 4);
}
