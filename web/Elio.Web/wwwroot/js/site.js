// Small extras only. The page reads the same with this file missing.
(function () {
  'use strict';

  // A "Copy" button on every command, where the clipboard is available.
  if (navigator.clipboard && window.isSecureContext) {
    document.querySelectorAll('.term').forEach(function (term) {
      var title = term.querySelector('.term-title');
      var code = term.querySelector('pre code');
      if (!title || !code) return;

      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'copy';
      button.textContent = 'Copy';
      button.setAttribute('aria-label', 'Copy: ' + title.textContent.trim());
      button.addEventListener('click', function () {
        // The prompt sign is decoration, not part of the command.
        var text = code.textContent.replace(/^\$ /gm, '').trim();
        navigator.clipboard.writeText(text).then(function () {
          button.textContent = 'Copied';
          setTimeout(function () { button.textContent = 'Copy'; }, 1600);
        }, function () { });
      });
      title.appendChild(button);
    });
  }

  // The section being read is marked in the header.
  if ('IntersectionObserver' in window) {
    var links = {};
    document.querySelectorAll('.site-nav a[href^="#"]').forEach(function (link) {
      links[link.getAttribute('href').slice(1)] = link;
    });

    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        Object.keys(links).forEach(function (id) {
          if (id === entry.target.id) links[id].setAttribute('aria-current', 'true');
          else links[id].removeAttribute('aria-current');
        });

        // On a phone the links are a strip that scrolls sideways: keep the current one in sight.
        var current = links[entry.target.id], strip = current.parentElement;
        if (strip.scrollWidth > strip.clientWidth) strip.scrollLeft = current.offsetLeft - strip.offsetLeft - 12;
      });
    }, { rootMargin: '-15% 0px -80% 0px' });

    Object.keys(links).forEach(function (id) {
      var section = document.getElementById(id);
      if (section) observer.observe(section);
    });
  }
})();
