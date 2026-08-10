/**
 * PierceView landing v6
 * - No auto-jump on load
 * - Discrete points fly-in after modest scroll
 * - F8 portal: view / select / click / scroll -1; image-only drag-drop
 * - Logo/card left edges share shell padding
 */
(() => {
  "use strict";

  // Prevent browser restore / jump to footer
  try {
    if ("scrollRestoration" in history) history.scrollRestoration = "manual";
  } catch {
    /* ignore */
  }

  const COUNTER_NS = "pierceview-landing-v1";
  const COUNTER_BASE = "https://api.counterapi.dev/v1";
  const KEYS = { views: "views", shares: "shares", github: "github" };
  const LS = {
    viewed: "pv_v6_viewed",
    cache: "pv_v6_stats",
    notes: "pv_v6_notes",
  };

  // Reel: while phase===demo, lock scroll near top; after points, allow full page
  const DEMO_LOCK_MAX = 48; // px free scroll before auto-fly points
  const POINTS_TRACK = 0.42; // fraction of reel height reserved after points for leaving to footer

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

  function toast(msg) {
    const el = $("#toast");
    if (!el) return;
    el.textContent = msg;
    el.hidden = false;
    requestAnimationFrame(() => el.classList.add("show"));
    clearTimeout(toast._t);
    toast._t = setTimeout(() => {
      el.classList.remove("show");
      setTimeout(() => {
        el.hidden = true;
      }, 220);
    }, 1600);
  }

  function formatCount(n) {
    if (n == null || Number.isNaN(n)) return "—";
    if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, "") + "M";
    if (n >= 1e4) return Math.round(n / 1e3) + "k";
    if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, "") + "k";
    return String(n);
  }

  function readCache() {
    try {
      return JSON.parse(localStorage.getItem(LS.cache) || "{}");
    } catch {
      return {};
    }
  }

  function writeCache(p) {
    const n = { ...readCache(), ...p, t: Date.now() };
    localStorage.setItem(LS.cache, JSON.stringify(n));
    return n;
  }

  function paintStats(stats) {
    for (const [k, v] of Object.entries(stats)) {
      const el = document.querySelector(`[data-stat="${k}"]`);
      if (el && v != null) el.textContent = formatCount(v);
    }
  }

  function setStatus(t) {
    const el = $("#statsStatus");
    if (el) el.textContent = t;
  }

  async function counterGet(key) {
    const res = await fetch(`${COUNTER_BASE}/${COUNTER_NS}/${key}/`, {
      mode: "cors",
    });
    if (!res.ok) throw new Error("get");
    const d = await res.json();
    return Number(d.count ?? d.value ?? 0);
  }

  async function counterUp(key) {
    const res = await fetch(`${COUNTER_BASE}/${COUNTER_NS}/${key}/up`, {
      mode: "cors",
    });
    if (!res.ok) throw new Error("up");
    const d = await res.json();
    return Number(d.count ?? d.value ?? 0);
  }

  async function bump(key) {
    const local = (Number(readCache()[key]) || 0) + 1;
    writeCache({ [key]: local });
    paintStats({ [key]: local });
    try {
      const remote = await counterUp(key);
      writeCache({ [key]: remote });
      paintStats({ [key]: remote });
      setStatus("计数已同步");
      return remote;
    } catch {
      setStatus("本机累计");
      return local;
    }
  }

  async function trackView() {
    const c = readCache();
    paintStats({
      views: c.views ?? 0,
      shares: c.shares ?? 0,
      github: c.github ?? 0,
    });
    if (sessionStorage.getItem(LS.viewed) === "1") {
      try {
        const [views, shares, github] = await Promise.all([
          counterGet(KEYS.views),
          counterGet(KEYS.shares),
          counterGet(KEYS.github),
        ]);
        writeCache({ views, shares, github });
        paintStats({ views, shares, github });
        setStatus("计数已同步");
      } catch {
        setStatus("本机累计");
      }
      return;
    }
    sessionStorage.setItem(LS.viewed, "1");
    await bump(KEYS.views);
  }

  function initShareGithub() {
    $("#btnShare")?.addEventListener("click", async () => {
      try {
        if (navigator.share) {
          await navigator.share({
            title: "寸镜 / PierceView",
            text: "Hold F8 · See · Act · Bring back",
            url: location.href,
          });
          await bump(KEYS.shares);
          toast("已分享");
          return;
        }
      } catch (e) {
        if (e?.name === "AbortError") return;
      }
      try {
        await navigator.clipboard.writeText(location.href);
        await bump(KEYS.shares);
        toast("链接已复制");
      } catch {
        toast("请手动复制链接");
      }
    });
    $$('a[href*="github.com/Nixz0824/PierceView"]').forEach((a) => {
      a.addEventListener("click", () => bump(KEYS.github));
    });
  }

  function esc(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function escAttr(s) {
    return String(s).replace(/"/g, "&quot;");
  }

  function loadNotes() {
    try {
      return JSON.parse(localStorage.getItem(LS.notes) || "[]");
    } catch {
      return [];
    }
  }

  function renderNotes() {
    const ul = $("#noteList");
    if (!ul) return;
    ul.innerHTML = loadNotes()
      .slice(0, 10)
      .map(
        (n) =>
          `<li><strong>${esc(n.name || "访客")}</strong>${esc(n.body)}</li>`
      )
      .join("");
  }

  function initNotes() {
    renderNotes();
    $("#noteForm")?.addEventListener("submit", (e) => {
      e.preventDefault();
      const name = ($("#noteName")?.value || "").trim().slice(0, 20);
      const body = ($("#noteBody")?.value || "").trim().slice(0, 120);
      if (!body) return;
      const list = loadNotes();
      list.unshift({ name, body, t: Date.now() });
      localStorage.setItem(LS.notes, JSON.stringify(list.slice(0, 24)));
      e.target.reset();
      renderNotes();
      toast("已留下（仅本机）");
    });
  }

  // ── Phase machine: demo | points | free (footer) ──
  function initPhases() {
    const reel = $("#reel");
    const stage = $("#stage");
    const points = $("#pointsLayer");
    if (!reel || !stage) return;

    /** @type {'demo' | 'points' | 'free'} */
    let phase = "demo";
    let animating = false;
    let wheelAcc = 0;

    function setPhase(next) {
      if (phase === next) return;
      phase = next;
      const isPoints = next === "points" || next === "free";
      stage.classList.toggle("is-points", isPoints);
      if (points) points.setAttribute("aria-hidden", isPoints ? "false" : "true");
    }

    function flyToPoints() {
      if (phase !== "demo" || animating) return;
      animating = true;
      setPhase("points");
      // settle scroll so user is mid-reel (not bottom)
      const pinH = window.innerHeight;
      const target = Math.round(pinH * 0.55);
      window.scrollTo({ top: target, behavior: "auto" });
      setTimeout(() => {
        animating = false;
        phase = "points";
      }, 780);
    }

    function backToDemo() {
      if (phase === "demo" || animating) return;
      animating = true;
      setPhase("demo");
      window.scrollTo({ top: 0, behavior: "auto" });
      wheelAcc = 0;
      setTimeout(() => {
        animating = false;
        phase = "demo";
      }, 500);
    }

    // Wheel: accumulate; small scroll → points fly in fully
    window.addEventListener(
      "wheel",
      (e) => {
        if (animating) {
          e.preventDefault();
          return;
        }

        // When F8 portal open, let wheel go to portal content if over portal
        if (stage.classList.contains("is-open")) {
          return; // portal handler may stopPropagation
        }

        const y = window.scrollY || 0;

        if (phase === "demo") {
          if (e.deltaY > 0) {
            wheelAcc += e.deltaY;
            e.preventDefault();
            if (wheelAcc > 90) {
              wheelAcc = 0;
              flyToPoints();
            }
          } else {
            wheelAcc = Math.max(0, wheelAcc + e.deltaY);
            if (y > 0) window.scrollTo(0, 0);
            e.preventDefault();
          }
          return;
        }

        if (phase === "points") {
          if (e.deltaY < 0 && y <= pinScrollTop() + 8) {
            e.preventDefault();
            backToDemo();
            return;
          }
          // scrolling down: allow free scroll into footer
          if (e.deltaY > 0) {
            phase = "free";
          }
          return;
        }

        // free: if user scrolls back into upper reel, return to points/demo
        if (phase === "free" && e.deltaY < 0 && y < pinScrollTop() + 40) {
          phase = "points";
          setPhase("points");
        }
      },
      { passive: false }
    );

    function pinScrollTop() {
      // approximate scroll position while points showing inside sticky pin
      return Math.round(window.innerHeight * 0.55);
    }

    // Touch swipe down support
    let touchY = 0;
    window.addEventListener(
      "touchstart",
      (e) => {
        touchY = e.touches[0]?.clientY || 0;
      },
      { passive: true }
    );
    window.addEventListener(
      "touchend",
      (e) => {
        const y2 = e.changedTouches[0]?.clientY || 0;
        const dy = touchY - y2;
        if (phase === "demo" && dy > 48) flyToPoints();
        else if (phase === "points" && dy < -48 && window.scrollY < 20)
          backToDemo();
      },
      { passive: true }
    );

    // Keyboard page down
    window.addEventListener("keydown", (e) => {
      if (e.code === "PageDown" || e.code === "ArrowDown") {
        if (phase === "demo") {
          e.preventDefault();
          flyToPoints();
        }
      }
      if (e.code === "PageUp" || e.code === "ArrowUp") {
        if (phase === "points" && window.scrollY <= pinScrollTop() + 8) {
          e.preventDefault();
          backToDemo();
        }
      }
      if (e.code === "Home") {
        e.preventDefault();
        backToDemo();
      }
    });

    // Guard against any residual scroll on load
    const hardTop = () => {
      window.scrollTo(0, 0);
      setPhase("demo");
      phase = "demo";
      wheelAcc = 0;
    };
    hardTop();
    requestAnimationFrame(hardTop);
    setTimeout(hardTop, 0);
    setTimeout(hardTop, 50);
    window.addEventListener("load", hardTop);

    // If browser forces scroll mid-frame, pull back while still demo
    window.addEventListener(
      "scroll",
      () => {
        if (phase === "demo" && window.scrollY > DEMO_LOCK_MAX) {
          // treat as intent to see points
          if (!animating) flyToPoints();
        }
      },
      { passive: true }
    );
  }

  // ── Portal ──
  function initPortal() {
    const stage = $("#stage");
    const backShell = $("#backShell");
    const portal = $("#portal");
    const portalScroll = $("#portalScroll");
    const ghost = $("#ghost");
    const dropzone = $("#dropzone");
    const dzIdle = $("#dzIdle");
    const dzBody = $("#dzBody");
    if (!stage || !backShell || !portal || !portalScroll) return;

    // Clone -1 page once
    portalScroll.replaceChildren();
    const mirror = backShell.cloneNode(true);
    mirror.removeAttribute("id");
    mirror.querySelectorAll("[id]").forEach((n) => n.removeAttribute("id"));
    portalScroll.appendChild(mirror);

    // Wire click buttons on original + clone
    function wireClicks(root) {
      root.querySelectorAll("[data-click]").forEach((btn) => {
        btn.addEventListener("click", (e) => {
          e.preventDefault();
          e.stopPropagation();
          const kind = btn.getAttribute("data-click");
          const fb =
            root.querySelector(".click-feedback") || $("#clickFeedback");
          if (fb) {
            fb.hidden = false;
            fb.textContent =
              kind === "saved"
                ? "已收藏（演示） · Saved (demo)"
                : "标题已复制到剪贴板 · Title copied";
          }
          if (kind === "copied") {
            const title =
              root.querySelector("h2")?.textContent?.trim() || "春日行程草案";
            navigator.clipboard?.writeText(title).catch(() => {});
          }
          toast(kind === "saved" ? "已点击：收藏" : "已点击：复制标题");
        });
      });
    }
    wireClicks(backShell);
    wireClicks(mirror);

    let open = false;
    let holdKey = false;
    let stageW = 1;
    let stageH = 1;
    let stageLeft = 0;
    let stageTop = 0;
    let x = 0;
    let y = 0;
    let ptrX = 0;
    let ptrY = 0;
    let portalPx = 200;
    let rafId = 0;
    let dirty = false;

    /** @type {null | { type: string, src?: string, name?: string }} */
    let payload = null;
    let dragging = false;
    let overDrop = false;

    // Sync scroll position: when scrolling inside portal mirror, keep original optional
    const portalDocScroll = mirror.querySelector(".webdoc-scroll");

    function measure() {
      const r = stage.getBoundingClientRect();
      stageW = r.width || 1;
      stageH = r.height || 1;
      stageLeft = r.left;
      stageTop = r.top;
      const raw = getComputedStyle(document.documentElement)
        .getPropertyValue("--portal")
        .trim();
      portalPx = parseFloat(raw) || 200;
      portalScroll.style.width = stageW + "px";
      portalScroll.style.height = stageH + "px";
    }

    function clamp(lx, ly) {
      const pad = portalPx * 0.32;
      x = Math.min(stageW - pad, Math.max(pad, lx));
      y = Math.min(stageH - pad, Math.max(pad, ly));
    }

    function paint() {
      const half = portalPx * 0.5;
      portal.style.transform =
        "translate3d(" + (x - half) + "px," + (y - half) + "px,0)";
      portalScroll.style.transform =
        "translate3d(" + (-x + half) + "px," + (-y + half) + "px,0)";
      stage.style.setProperty("--mx", (x / stageW) * 100 + "%");
      stage.style.setProperty("--my", (y / stageH) * 100 + "%");
    }

    function hitDrop(cx, cy) {
      if (!dropzone) return;
      const r = dropzone.getBoundingClientRect();
      overDrop =
        cx >= r.left && cx <= r.right && cy >= r.top && cy <= r.bottom;
      dropzone.classList.toggle("is-hot", dragging && overDrop);
    }

    function requestPaint() {
      dirty = true;
      if (rafId) return;
      rafId = requestAnimationFrame(() => {
        rafId = 0;
        if (!dirty) return;
        dirty = false;
        if (dragging && ghost) {
          ghost.style.left = ptrX + "px";
          ghost.style.top = ptrY + "px";
          hitDrop(ptrX, ptrY);
        }
        if (open && holdKey && !isScrollingPortal) {
          clamp(ptrX - stageLeft, ptrY - stageTop);
          paint();
        } else if (open && holdKey) {
          // still update mask while scrolling? keep last x,y
          paint();
        }
      });
    }

    let isScrollingPortal = false;
    let scrollTimer = 0;

    // Wheel over portal → scroll the -1 document inside circle
    portal.addEventListener(
      "wheel",
      (e) => {
        if (!open || !holdKey) return;
        const sc = portalDocScroll;
        if (!sc) return;
        e.preventDefault();
        e.stopPropagation();
        isScrollingPortal = true;
        sc.scrollTop += e.deltaY;
        clearTimeout(scrollTimer);
        scrollTimer = setTimeout(() => {
          isScrollingPortal = false;
        }, 120);
      },
      { passive: false }
    );

    function openPortal() {
      if (open) return;
      measure();
      open = true;
      stage.classList.add("is-open");
      portal.hidden = false;
      if (ptrX || ptrY) clamp(ptrX - stageLeft, ptrY - stageTop);
      else clamp(stageW * 0.5, stageH * 0.48);
      paint();
    }

    function endDrag() {
      dragging = false;
      payload = null;
      overDrop = false;
      if (ghost) {
        ghost.hidden = true;
        ghost.innerHTML = "";
      }
      document.body.style.userSelect = "";
      dropzone?.classList.remove("is-hot");
    }

    function commitDrop(p) {
      if (!dzBody || !dzIdle) return;
      dzIdle.hidden = true;
      dzBody.hidden = false;
      dzBody.innerHTML =
        '<img class="thumb" src="' +
        escAttr(p.src || "") +
        '" alt="" /><div>' +
        esc(p.name || "image") +
        "</div>";
      endDrag();
    }

    function closePortal() {
      if (dragging && payload) {
        if (overDrop) {
          commitDrop(payload);
          toast("图片已投放");
        } else {
          toast("已取消");
          endDrag();
        }
      } else {
        endDrag();
      }
      open = false;
      holdKey = false;
      stage.classList.remove("is-open");
      portal.hidden = true;
    }

    function stageInView() {
      const r = stage.getBoundingClientRect();
      return r.bottom > 100 && r.top < window.innerHeight - 50;
    }

    window.addEventListener(
      "pointermove",
      (e) => {
        ptrX = e.clientX;
        ptrY = e.clientY;
        if (open || dragging) requestPaint();
      },
      { passive: true }
    );

    window.addEventListener("keydown", (e) => {
      if (e.code !== "F8" && e.code !== "Space") return;
      const tag = e.target?.tagName || "";
      if (tag === "INPUT" || tag === "TEXTAREA" || e.target?.isContentEditable)
        return;
      if (!stageInView()) return;
      if (stage.classList.contains("is-points")) return; // no portal during points phase
      e.preventDefault();
      if (holdKey) return;
      holdKey = true;
      measure();
      openPortal();
      requestPaint();
    });

    window.addEventListener("keyup", (e) => {
      if (e.code !== "F8" && e.code !== "Space") return;
      if (!holdKey && !open) return;
      e.preventDefault();
      closePortal();
    });

    window.addEventListener("blur", () => {
      if (open || holdKey) closePortal();
    });

    // Image-only drag from portal
    portalScroll.addEventListener(
      "pointerdown",
      (e) => {
        if (!open || !holdKey || e.button !== 0) return;

        // allow text selection / buttons / scroll without starting image drag
        const fig = e.target.closest("[data-drag-image], .web-fig");
        if (!fig) return;

        e.preventDefault();
        const img = fig.querySelector("img");
        beginDrag(
          {
            type: "image",
            src: img?.getAttribute("src") || "",
            name:
              fig.querySelector("figcaption")?.textContent?.trim() || "image",
          },
          e
        );
      },
      { passive: false }
    );

    function beginDrag(p, e) {
      if (!holdKey || !open) return;
      payload = p;
      dragging = true;
      document.body.style.userSelect = "none";
      try {
        window.getSelection()?.removeAllRanges();
      } catch {
        /* ignore */
      }
      if (ghost) {
        ghost.hidden = false;
        ghost.innerHTML =
          '<img src="' +
          escAttr(p.src || "") +
          '" alt="" /><div>' +
          esc(p.name || "") +
          "</div>";
        ghost.style.left = e.clientX + "px";
        ghost.style.top = e.clientY + "px";
      }
      hitDrop(e.clientX, e.clientY);
      requestPaint();
    }

    window.addEventListener(
      "resize",
      () => {
        if (!open) return;
        measure();
        clamp(x, y);
        paint();
      },
      { passive: true }
    );

    function clamp(lx, ly) {
      const pad = portalPx * 0.32;
      x = Math.min(stageW - pad, Math.max(pad, lx));
      y = Math.min(stageH - pad, Math.max(pad, ly));
    }

    function paint() {
      const half = portalPx * 0.5;
      portal.style.transform =
        "translate3d(" + (x - half) + "px," + (y - half) + "px,0)";
      portalScroll.style.transform =
        "translate3d(" + (-x + half) + "px," + (-y + half) + "px,0)";
      stage.style.setProperty("--mx", (x / stageW) * 100 + "%");
      stage.style.setProperty("--my", (y / stageH) * 100 + "%");
    }
  }

  function boot() {
    // Force top before anything else
    window.scrollTo(0, 0);
    initShareGithub();
    initNotes();
    initPhases();
    initPortal();
    trackView();
    window.scrollTo(0, 0);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
