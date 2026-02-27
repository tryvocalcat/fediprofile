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
    const cat = link.category || '';
    if (!categories[cat]) {
      categories[cat] = [];
    }
    categories[cat].push(link);
  });

  // Render by category — uncategorized first, without a heading
  const sortedEntries = Object.entries(categories).sort(([a], [b]) => {
    if (a === '') return -1;
    if (b === '') return 1;
    return 0;
  });
  sortedEntries.forEach(([cat, catLinks]) => {
    html += cat ? `<div class="category"><h2>${escapeHtml(cat)}</h2>` : '<div class="category">';
    catLinks.forEach(link => {
      const url = link.href || '#';
      const iconUrl = link.icon?.url;
      html += `
        <a href="${escapeHtml(url)}" class="link-card" data-type="${escapeHtml(link.type || 'link')}">
          ${iconUrl ? `<img src="${escapeHtml(iconUrl)}" alt="" class="link-icon" />` : ''}
          <div class="link-content">
            <strong>${escapeHtml(link.name)}</strong>
            ${link.description ? `<p>${escapeHtml(link.description)}</p>` : ''}
          </div>
          ${link.autoBoost ? '<span class="boost-badge">Boosted</span>' : ''}
        </a>
      `;
    });
    html += '</div>';
  });

  html += '</div>';
  container.innerHTML = html;
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

// Populate the fediverse handle
function populateFediHandle() {
  const el = document.getElementById('fedi-address');
  if (el) {
    const username = window.location.pathname.replace(/^\/|\/$/, '');
    const domain = window.location.host;
    el.textContent = username + '@' + domain;
  }
}

// Copy fedi address to clipboard
function initCopyButton() {
  const btn = document.getElementById('copy-fedi');
  if (!btn) return;
  btn.addEventListener('click', () => {
    const address = document.getElementById('fedi-address')?.textContent;
    if (!address) return;
    navigator.clipboard.writeText(address).then(() => {
      btn.textContent = '✅';
      setTimeout(() => { btn.textContent = '📋'; }, 1500);
    });
  });
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', () => {
  loadProfile();
  populateFediHandle();
  initCopyButton();
});
