// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useMemo, useRef, useState } from "react";
import { cx } from "./styles";

export interface PickerOption {
  id: string;
  /** Shown as the main line, and what the typed text is matched against first. */
  label: string;
  /** Extra text matched by search but shown quietly, e.g. a short name or key. */
  detail?: string;
  /** A short tag shown before the label, e.g. "Room". */
  tag?: string;
  /** A note shown after the label, e.g. why this one needs saving first. */
  note?: string;
}

/** Anywhere in the label, detail or tag — not just the start, so "wio" finds "KQ4WLR Wio L1". */
function matches(option: PickerOption, needle: string): boolean {
  if (!needle) return true;
  const haystack = `${option.label} ${option.detail ?? ""} ${option.tag ?? ""}`.toLowerCase();
  return needle
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}

/**
 * A type-to-search picker. A plain <select> only jumps to entries by first
 * letter, which is useless with hundreds of contacts, so this filters as you
 * type and matches anywhere in the text.
 */
export function Picker(props: {
  label: string;
  value: string;
  options: PickerOption[];
  disabled?: boolean;
  /** Shown when nothing is typed and nothing is chosen. */
  placeholder?: string;
  /** Shown instead of the list when there is nothing to choose from. */
  emptyText?: string;
  onChange: (id: string) => void;
}) {
  const { options, value, disabled } = props;
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const box = useRef<HTMLDivElement>(null);

  const chosen = options.find((o) => o.id === value) ?? null;
  const needle = query.trim().toLowerCase();
  const shown = useMemo(() => options.filter((o) => matches(o, needle)), [options, needle]);

  useEffect(() => setActive(0), [needle, open]);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);

  const choose = (id: string) => {
    props.onChange(id);
    setQuery("");
    setOpen(false);
  };

  const placeholder =
    props.placeholder ??
    (options.length > 12 ? `Type to search ${options.length}…` : "Type to search, or pick below");

  return (
    <div className={cx("field")} ref={box}>
      <span id={`${props.label}-label`}>{props.label}</span>
      <div className={cx("picker")}>
        <input
          className={cx("input")}
          role="combobox"
          aria-expanded={open}
          aria-controls={`${props.label}-list`}
          aria-labelledby={`${props.label}-label`}
          autoComplete="off"
          disabled={disabled}
          value={open ? query : (chosen?.label ?? "")}
          placeholder={disabled ? (props.emptyText ?? "") : placeholder}
          onFocus={() => setOpen(true)}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onKeyDown={(e) => {
            if (e.key === "ArrowDown" || e.key === "ArrowUp") {
              e.preventDefault();
              setOpen(true);
              setActive((i) => {
                const next = e.key === "ArrowDown" ? i + 1 : i - 1;
                return Math.max(0, Math.min(shown.length - 1, next));
              });
            } else if (e.key === "Enter" && open && shown[active]) {
              e.preventDefault();
              choose(shown[active].id);
            } else if (e.key === "Escape" && open) {
              e.preventDefault();
              setOpen(false);
              setQuery("");
            }
          }}
        />
        {open && !disabled && (
          <ul className={cx("picker-list")} id={`${props.label}-list`} role="listbox">
            {shown.length === 0 && (
              <li className={`${cx("picker-empty")} ${cx("muted")}`}>
                {options.length === 0 ? (props.emptyText ?? "Nothing to choose yet.") : `Nothing matches “${query}”.`}
              </li>
            )}
            {shown.slice(0, 200).map((o, i) => (
              <li key={o.id}>
                <button
                  type="button"
                  role="option"
                  aria-selected={o.id === value}
                  className={`${cx("picker-item")} ${i === active ? cx("picker-item--active") : ""}`}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => choose(o.id)}
                >
                  <span className={cx("item-top")}>
                    {o.tag && <span className={cx("chip")}>{o.tag}</span>}
                    <span className={cx("item-title")}>{o.label}</span>
                  </span>
                  {(o.detail || o.note) && (
                    <span className={cx("item-preview")}>
                      {o.detail}
                      {o.detail && o.note ? " · " : ""}
                      {o.note}
                    </span>
                  )}
                </button>
              </li>
            ))}
            {shown.length > 200 && (
              <li className={`${cx("picker-empty")} ${cx("muted")}`}>
                Showing the first 200 of {shown.length}. Keep typing to narrow it down.
              </li>
            )}
          </ul>
        )}
      </div>
    </div>
  );
}
