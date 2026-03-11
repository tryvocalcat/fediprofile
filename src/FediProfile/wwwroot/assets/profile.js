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

  sortedEntries.forEach(([cat, catLinks]) => {
    html += cat ? `<div class="category"><h2>${escapeHtml(cat)}</h2>` : '<div class="category">';
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
