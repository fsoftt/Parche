// Language switcher: keep the current #section and remember the explicit choice.
(function () {
  var links = document.querySelectorAll(".lang a[data-lang]");
  function sync() {
    for (var i = 0; i < links.length; i++) {
      var a = links[i];
      if (!a.hasAttribute("aria-current")) {
        a.setAttribute("href", a.getAttribute("data-base") + location.hash);
      }
    }
  }
  for (var i = 0; i < links.length; i++) {
    (function (a) {
      a.setAttribute("data-base", a.getAttribute("href"));
      a.addEventListener("click", function () {
        try { localStorage.setItem("parche-lang", a.getAttribute("data-lang")); } catch (e) {}
      });
    })(links[i]);
  }
  window.addEventListener("hashchange", sync);
  sync();
})();
