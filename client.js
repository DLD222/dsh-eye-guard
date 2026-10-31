window.__ModuleLoader__.load({
  id: '@local/dsh-eye-guard',
  factory(require) {
    const React = require('react');
    const h = React.createElement;
    const { useEffect, useRef, useState } = React;

    const API = '/eye-guard/api';

    const CSS = `
      .eg-page {
        max-width: 680px;
        padding: 4px 0 32px;
        color: var(--dsw-alias-label-primary, #e8edf5);
      }
      .eg-kicker {
        margin: 0 0 6px;
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 11px;
        font-weight: 600;
        letter-spacing: .12em;
        text-transform: uppercase;
      }
      .eg-title {
        margin: 0;
        font-size: 20px;
        font-weight: 650;
        letter-spacing: -.01em;
      }
      .eg-lede {
        max-width: 590px;
        margin: 8px 0 0;
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 13px;
        line-height: 1.55;
      }
      .eg-status-grid {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 10px;
        margin: 20px 0 22px;
      }
      .eg-card {
        min-width: 0;
        padding: 13px 14px 14px;
        border: 1px solid var(--dsw-alias-border-l1, rgba(255,255,255,.1));
        border-radius: 12px;
        background: var(--dsw-alias-bg-layer-1, #151b24);
      }
      .eg-card-label {
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 11px;
        font-weight: 600;
        letter-spacing: .04em;
        text-transform: uppercase;
      }
      .eg-card-value {
        margin-top: 6px;
        font-size: 22px;
        font-weight: 650;
        font-variant-numeric: tabular-nums;
        letter-spacing: -.02em;
      }
      .eg-card-value small {
        margin-left: 4px;
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 11px;
        font-weight: 500;
        letter-spacing: 0;
      }
      .eg-card.is-offline .eg-card-value {
        color: var(--dsw-alias-state-warn-primary, #e8b44a);
        font-size: 16px;
        line-height: 1.35;
      }
      .eg-section {
        margin-top: 22px;
        padding-top: 20px;
        border-top: 1px solid var(--dsw-alias-border-l1, rgba(255,255,255,.1));
      }
      .eg-section-title {
        margin: 0 0 3px;
        font-size: 13px;
        font-weight: 650;
      }
      .eg-section-hint {
        margin: 0 0 14px;
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 12px;
        line-height: 1.5;
      }
      .eg-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 18px;
        padding: 11px 0;
        border-bottom: 1px solid var(--dsw-alias-border-l1, rgba(255,255,255,.07));
      }
      .eg-row:last-child { border-bottom: 0; }
      .eg-row-copy { min-width: 0; }
      .eg-row-label {
        font-size: 13px;
        font-weight: 550;
        line-height: 1.4;
      }
      .eg-row-hint {
        margin-top: 2px;
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 11px;
        line-height: 1.4;
      }
      .eg-input {
        flex: 0 0 auto;
        width: 92px;
        height: 34px;
        box-sizing: border-box;
        padding: 0 10px;
        border: 1px solid var(--dsw-alias-border-l2, rgba(255,255,255,.18));
        border-radius: 8px;
        background: var(--dsw-alias-bg-base, #0d1117);
        color: var(--dsw-alias-label-primary, #e8edf5);
        font: inherit;
        font-size: 13px;
        font-variant-numeric: tabular-nums;
        text-align: right;
      }
      .eg-input:focus-visible {
        outline: 2px solid var(--dsw-alias-brand-primary, #5aa9ff);
        outline-offset: 2px;
      }
      .eg-switch {
        position: relative;
        flex: 0 0 auto;
        display: inline-flex;
        width: 38px;
        height: 22px;
        cursor: pointer;
      }
      .eg-switch input {
        position: absolute;
        width: 1px;
        height: 1px;
        opacity: 0;
      }
      .eg-switch-track {
        position: absolute;
        inset: 0;
        border-radius: 999px;
        background: var(--dsw-alias-border-l2, rgba(255,255,255,.18));
        transition: background .16s ease;
      }
      .eg-switch-track::after {
        content: "";
        position: absolute;
        top: 3px;
        left: 3px;
        width: 16px;
        height: 16px;
        border-radius: 50%;
        background: #fff;
        transition: transform .16s ease;
      }
      .eg-switch input:checked + .eg-switch-track {
        background: var(--dsw-alias-brand-primary, #5aa9ff);
      }
      .eg-switch input:checked + .eg-switch-track::after {
        transform: translateX(16px);
      }
      .eg-switch input:focus-visible + .eg-switch-track {
        outline: 2px solid var(--dsw-alias-brand-primary, #5aa9ff);
        outline-offset: 2px;
      }
      .eg-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        margin-top: 20px;
      }
      .eg-button {
        height: 34px;
        padding: 0 14px;
        border-radius: 8px;
        font: inherit;
        font-size: 13px;
        font-weight: 600;
        cursor: pointer;
      }
      .eg-button:disabled { cursor: default; opacity: .5; }
      .eg-button-primary {
        border: 1px solid transparent;
        background: var(--dsw-alias-brand-primary, #5aa9ff);
        color: #fff;
      }
      .eg-button-secondary {
        border: 1px solid var(--dsw-alias-border-l2, rgba(255,255,255,.18));
        background: transparent;
        color: var(--dsw-alias-label-primary, #e8edf5);
      }
      .eg-button:focus-visible {
        outline: 2px solid var(--dsw-alias-brand-primary, #5aa9ff);
        outline-offset: 2px;
      }
      .eg-message {
        margin: 12px 0 0;
        font-size: 12px;
        line-height: 1.5;
      }
      .eg-message.is-error { color: var(--dsw-alias-state-error-primary, #ff6b6b); }
      .eg-message.is-ok { color: var(--dsw-alias-state-success-primary, #4cc38a); }
      .eg-note {
        margin: 18px 0 0;
        padding: 12px 14px;
        border: 1px solid var(--dsw-alias-border-l1, rgba(255,255,255,.1));
        border-radius: 10px;
        background: var(--dsw-alias-bg-layer-2, #111720);
        color: var(--dsw-alias-label-secondary, #9aa7b8);
        font-size: 11px;
        line-height: 1.55;
      }
      .eg-note strong { color: var(--dsw-alias-label-primary, #e8edf5); font-weight: 600; }
      @media (max-width: 560px) {
        .eg-status-grid { grid-template-columns: 1fr; }
      }
    `;

    const NUMBER_FIELDS = [
      ['intervalMinutes', 'Focus interval', 'Minutes between breaks.', 1, 120, 'minutes'],
      ['breakSeconds', 'Break length', 'How long the full-screen rest lasts.', 5, 300, 'seconds'],
      ['warningSeconds', 'Warning lead', 'Corner countdown before the full-screen overlay. Use 0 to skip it.', 0, 120, 'seconds'],
      ['idleResetMinutes', 'Idle reset', 'Away time that silently restarts the focus timer.', 1, 60, 'minutes'],
    ];

    function pad(value) {
      return String(value).padStart(2, '0');
    }

    function formatClock(milliseconds) {
      const total = Math.max(0, Math.ceil(milliseconds / 1000));
      return `${Math.floor(total / 60)}:${pad(total % 60)}`;
    }

    function numberValue(draft, key) {
      const value = draft[key];
      if (value === '') return '';
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : '';
    }

    function validateDraft(draft) {
      for (const [key, label, , min, max] of NUMBER_FIELDS) {
        const value = Number(draft[key]);
        if (!Number.isInteger(value) || value < min || value > max) {
          return `${label} must be a whole number between ${min} and ${max}.`;
        }
      }
      return '';
    }

    function StatusCard({ label, value, suffix, offline }) {
      return h('div', { className: `eg-card${offline ? ' is-offline' : ''}` },
        h('div', { className: 'eg-card-label' }, label),
        h('div', { className: 'eg-card-value' }, value, suffix ? h('small', null, suffix) : null),
      );
    }

    function NumberRow({ field, draft, disabled, onEdit }) {
      const [key, label, hint, min, max, unit] = field;
      const id = `eye-guard-${key}`;
      return h('div', { className: 'eg-row' },
        h('div', { className: 'eg-row-copy' },
          h('label', { className: 'eg-row-label', htmlFor: id }, label),
          h('div', { className: 'eg-row-hint', id: `${id}-hint` }, hint),
        ),
        h('input', {
          id,
          className: 'eg-input',
          type: 'number',
          inputMode: 'numeric',
          min,
          max,
          step: 1,
          value: numberValue(draft, key),
          disabled,
          'aria-describedby': `${id}-hint`,
          onChange: (event) => onEdit(key, event.target.value),
        }),
      );
    }

    function SwitchRow({ field, checked, disabled, onToggle }) {
      const [key, label, hint] = field;
      const id = `eye-guard-${key}`;
      return h('div', { className: 'eg-row' },
        h('div', { className: 'eg-row-copy' },
          h('label', { className: 'eg-row-label', htmlFor: id }, label),
          h('div', { className: 'eg-row-hint', id: `${id}-hint` }, hint),
        ),
        h('label', { className: 'eg-switch', htmlFor: id },
          h('input', {
            id,
            type: 'checkbox',
            checked,
            disabled,
            'aria-describedby': `${id}-hint`,
            onChange: (event) => onToggle(key, event.target.checked),
          }),
          h('span', { className: 'eg-switch-track', 'aria-hidden': 'true' }),
        ),
      );
    }

    function EyeGuardPage() {
      const [remote, setRemote] = useState(null);
      const [draft, setDraft] = useState(null);
      const [dirty, setDirty] = useState(false);
      const [now, setNow] = useState(Date.now());
      const [error, setError] = useState('');
      const [notice, setNotice] = useState('');
      const [saving, setSaving] = useState(false);
      const [starting, setStarting] = useState(false);
      const dirtyRef = useRef(false);
      const mountedRef = useRef(true);

      useEffect(() => {
        mountedRef.current = true;
        let reading = false;
        const read = async () => {
          if (reading) return;
          reading = true;
          try {
            const response = await fetch(`${API}/state`, { cache: 'no-store' });
            const data = await response.json();
            if (!mountedRef.current) return;
            if (!response.ok || !data.ok) throw new Error(data.error || 'Could not read Eye Guard state.');
            setRemote(data);
            if (!dirtyRef.current) setDraft({ ...data.config });
            setError('');
          } catch (failure) {
            if (mountedRef.current) setError(failure instanceof Error ? failure.message : 'Could not reach Eye Guard.');
          } finally {
            reading = false;
          }
        };
        void read();
        const poll = setInterval(read, 2000);
        const clock = setInterval(() => setNow(Date.now()), 1000);
        return () => {
          mountedRef.current = false;
          clearInterval(poll);
          clearInterval(clock);
        };
      }, []);

      const edit = (key, value) => {
        dirtyRef.current = true;
        setDirty(true);
        setNotice('');
        setDraft((current) => ({ ...current, [key]: value }));
      };

      const toggle = (key, value) => {
        dirtyRef.current = true;
        setDirty(true);
        setNotice('');
        setDraft((current) => ({ ...current, [key]: value }));
      };

      const save = async () => {
        const invalid = validateDraft(draft);
        if (invalid) {
          setError(invalid);
          setNotice('');
          return;
        }
        setSaving(true);
        setError('');
        setNotice('');
        try {
          const payload = {};
          for (const [key] of NUMBER_FIELDS) payload[key] = Number(draft[key]);
          payload.highContrast = Boolean(draft.highContrast);
          payload.reduceMotion = Boolean(draft.reduceMotion);
          const response = await fetch(`${API}/config`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify(payload),
          });
          const data = await response.json();
          if (!response.ok || !data.ok) throw new Error(data.error || 'The settings were not accepted.');
          setRemote(data);
          setDraft({ ...data.config });
          dirtyRef.current = false;
          setDirty(false);
          setNotice('Saved. The new rhythm applies immediately.');
        } catch (failure) {
          setError(failure instanceof Error ? failure.message : 'Could not save the settings.');
        } finally {
          setSaving(false);
        }
      };

      const startBreak = async () => {
        setStarting(true);
        setError('');
        setNotice('');
        try {
          const response = await fetch(`${API}/break`, { method: 'POST' });
          const data = await response.json();
          if (!response.ok || !data.ok) throw new Error(data.error || 'Could not start a break.');
          setRemote(data);
          setNotice('Break starting now. Hold Esc for 3 seconds only if you must stop it.');
        } catch (failure) {
          setError(failure instanceof Error ? failure.message : 'Could not start a break.');
        } finally {
          setStarting(false);
        }
      };

      const resetStats = async () => {
        if (!window.confirm('Reset today’s break count and your streak? This only changes the local Eye Guard statistics.')) return;
        setError('');
        setNotice('');
        try {
          const response = await fetch(`${API}/stats/reset`, { method: 'POST' });
          const data = await response.json();
          if (!response.ok || !data.ok) throw new Error(data.error || 'Could not reset the statistics.');
          setRemote(data);
          setNotice('Statistics reset.');
        } catch (failure) {
          setError(failure instanceof Error ? failure.message : 'Could not reset the statistics.');
        }
      };

      if (!remote || !draft) {
        return h('div', { className: 'eg-page' },
          h('style', null, CSS),
          h('p', { className: 'eg-kicker' }, 'Eye Guard'),
          h('h2', { className: 'eg-title' }, 'Eye Guard'),
          h('p', { className: 'eg-lede' }, 'Loading the eye-break engine…'),
        );
      }

      const phase = remote.phase;
      const resting = phase === 'break';
      const offline = !remote.helperConnected;
      const remains = resting && typeof remote.status?.breakEndsAt === 'number'
        ? remote.status.breakEndsAt - now
        : typeof remote.nextBreakAt === 'number'
          ? remote.nextBreakAt - now
          : null;
      const nextValue = offline ? 'Helper offline' : resting ? 'Resting now' : remains === null ? '—' : formatClock(remains);
      const nextSuffix = offline || resting || remains === null ? '' : 'until break';

      return h('div', { className: 'eg-page' },
        h('style', null, CSS),
        h('p', { className: 'eg-kicker' }, 'Focus care'),
        h('h2', { className: 'eg-title' }, 'Eye Guard'),
        h('p', { className: 'eg-lede' },
          'A strict 20-20-20 rhythm above every Windows app. At each break the screen dims, the tray shows the countdown, and the overlay cannot be closed until the timer ends.',
        ),

        h('div', { className: 'eg-status-grid' },
          h(StatusCard, { label: 'Next break', value: nextValue, suffix: nextSuffix, offline }),
          h(StatusCard, { label: 'Today', value: remote.stats.completed, suffix: remote.stats.completed === 1 ? 'break' : 'breaks' }),
          h(StatusCard, { label: 'Streak', value: remote.stats.streak, suffix: remote.stats.streak === 1 ? 'day' : 'days' }),
        ),

        h('div', { className: 'eg-section' },
          h('h3', { className: 'eg-section-title' }, 'Rhythm'),
          h('p', { className: 'eg-section-hint' }, 'Changes apply immediately and restart the current focus interval. There is no pause and no snooze.'),
          NUMBER_FIELDS.map((field) => h(NumberRow, {
            key: field[0],
            field,
            draft,
            disabled: saving || offline,
            onEdit: edit,
          })),
        ),

        h('div', { className: 'eg-section' },
          h('h3', { className: 'eg-section-title' }, 'Comfort'),
          h('p', { className: 'eg-section-hint' }, 'The overlay follows your Windows high-contrast setting automatically; these switches add an explicit preference.'),
          h(SwitchRow, {
            field: ['highContrast', 'High-contrast overlay', 'Near-black background, white type, and a yellow accent.'],
            checked: Boolean(draft.highContrast),
            disabled: saving || offline,
            onToggle: toggle,
          }),
          h(SwitchRow, {
            field: ['reduceMotion', 'Reduce motion', 'Stops the slow glow pulse. Animations also stop when Windows animations are off.'],
            checked: Boolean(draft.reduceMotion),
            disabled: saving || offline,
            onToggle: toggle,
          }),
        ),

        h('div', { className: 'eg-actions' },
          h('button', {
            className: 'eg-button eg-button-primary',
            type: 'button',
            disabled: !dirty || saving || offline,
            onClick: save,
          }, saving ? 'Saving…' : 'Save changes'),
          h('button', {
            className: 'eg-button eg-button-secondary',
            type: 'button',
            disabled: starting || offline,
            onClick: startBreak,
          }, starting ? 'Starting…' : 'Start a break now'),
          h('button', {
            className: 'eg-button eg-button-secondary',
            type: 'button',
            disabled: offline,
            onClick: resetStats,
          }, 'Reset statistics'),
        ),

        error ? h('p', { className: 'eg-message is-error', role: 'alert' }, error) : null,
        notice ? h('p', { className: 'eg-message is-ok', role: 'status' }, notice) : null,

        h('p', { className: 'eg-note' },
          h('strong', null, 'Strict by design. '),
          'The break overlay stays above normal windows on every monitor and blocks closing. Hold Esc for 3 seconds for the emergency exit; an emergency exit is not counted toward today’s breaks. Meeting or full-screen video is detected and the break is postponed until it ends, then taken once. All settings and statistics stay on this computer.',
        ),
      );
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        ctx.effect(() => ctx.slots.inject('settings.section', () => ctx.slots.register({
          name: 'settings.section',
          id: 'eye-guard',
          order: 40,
          label: 'Eye Guard',
        }, EyeGuardPage)), 'eye-guard: settings page');
      },
    };
  },
});
