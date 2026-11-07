---
name: antigravity-bridge
description: Turbo Computer Vision, Mouse/Keyboard HID control, Silent-Mode background window automation, Cyan-to-Blue Visual HUD, and Mandatory User Consent Guard for Windows via the Antigravity Bridge MCP server.
---

# Antigravity Computer-Use & Silent-Mode Bridge Skill (`v0.3 Shield & Aura`)

Use the `antigravity-bridge` MCP tools whenever the user asks you to see the screen, control the mouse or keyboard, or automate external Windows applications in the background (**Silent-Mode**).

---

## 🛡️ ОБЯЗАТЕЛЬНОЕ ПРАВИЛО СОГЛАСИЯ (MANDATORY CONSENT POLICY)

> [!IMPORTANT]
> **Данный скилл ОБЯЗАН запрашивать разрешение пользователя в двух случаях:**
> 1. **Открытие любых внешних программ (`launch_app` / запуск процессов)**:
>    - Перед открытием любой программы обязательно спроси согласие у пользователя (в чате или вызовом `request_permission(target_program=..., reason=..., elevate=False)` / `launch_app(..., reason=...)`, который открывает на экране нативное окно согласия с переливающейся голубо-синей рамкой).
>    - Если пользователь нажал **«✕ Запретить»** (`allowed: false` / `PermissionDenied`), немедленно прекрати запуск и сообщи об этом пользователю.
> 2. **Повышение прав / UAC / Установка программ (`elevate=True`, инсталляторы, системные утилиты)**:
>    - Перед любым действием, требующим прав Администратора (установка ПО, `runas`, реестр, сетевые настройки, системные службы), **обязательно** запроси разрешение на повышение прав: `request_permission(target_program=..., operation_type="Повышение прав (Admin / UAC)", elevate=True, reason=...)` (или укажи в чате, зачем требуются права администратора).
>    - Никогда не обходи отказ пользователя и не используй скрытые ключи без предварительного согласия.

---

## 🌌 Визуализация Computer-Use (Cyan ↔ Deep Blue Shimmering HUD)

Во время работы с компьютером шлюз автоматически включает визуальную индикацию в стиле Claude Computer-Use:
- По краям экрана переливается **голубая ↔ темно-синяя неоновая рамка (`#00EEFF` ↔ `#2563EB`, 30 FPS)**, полностью прозрачная для кликов мыши (`WS_EX_TRANSPARENT`).
- В верхней части экрана отображается плашка статуса **`ANTIGRAVITY COMPUTER-USE • [Текущее действие]`**.
- В точках клика мыши отрисовывается расходящийся голубо-синий импульс.
- Ты можешь управлять статусом на плашке через `set_overlay(visible=True, status_text="...")`.

---

## ⚡ Speed Rule: Используй `execute_batch` для многошаговых действий

Объединяй последовательности действий (`mouse_click`, `keyboard_type`, `keyboard_hotkey`, `wait_window`, `silent_click`, `silent_type`, `silent_automate_element`, `manage_window`) в единый вызов `execute_batch(steps=[...], capture_after=True)` для выполнения за 1 ход (<150 мс).

## Workflow Guidelines

1. **Silent-Mode для фоновых окон (`silent_*` / `window_capture_silent`)**
   - Вызови `list_windows` (<1.5 мс), чтобы найти `hwnd` нужного окна.
   - Используй `window_capture_silent(hwnd=..., annotate_ui=True)` (~14 мс) для получения снимка фонового окна с разметкой контролов `[1..N]` без перехвата фокуса.
   - Используй `silent_automate_element`, `silent_click` и `silent_type`, чтобы физический курсор пользователя оставался свободным.
2. **Полный экран и активный ввод**
   - Вызывай `screen_capture(draw_grid=True)` (~49 мс) для просмотра экрана с координатной сеткой `(X, Y)` и голубо-синей рамкой безопасности.
   - Увод физического курсора мыши в левый верхний угол `(0, 0)` мгновенно активирует аппаратный **Fail-Safe**.
