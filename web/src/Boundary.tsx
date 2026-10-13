// SPDX-License-Identifier: GPL-3.0-or-later
import { Component, type ErrorInfo, type ReactNode } from "react";
import { cx } from "./styles";

/**
 * Stops a fault in this panel reaching the rest of Zeus.
 *
 * Zeus renders a feature's panel inside its own React tree. Without a boundary of our own, an
 * exception thrown while rendering unmounts whatever tree it propagates into — so a bug here
 * does not produce a broken panel, it takes the window with it, and the operator loses their
 * workspace because of a feature they were only looking at. A feature has no business doing
 * that to its host.
 *
 * A caught error leaves a panel that explains itself and can be copied into a bug report,
 * which is also far more useful than a vanished window.
 */
export class Boundary extends Component<{ children: ReactNode }, { error: Error | null; where: string }> {
  state: { error: Error | null; where: string } = { error: null, where: "" };

  static getDerivedStateFromError(error: Error) {
    return { error, where: "" };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    this.setState({ error, where: info.componentStack ?? "" });
    // Zeus's own console is where someone looking for this will look first.
    console.error("[Mesh Messenger] the panel failed to render", error, info.componentStack);
  }

  private report(): string {
    const e = this.state.error;
    return [
      `Mesh Messenger panel error — ${new Date().toISOString()}`,
      `${e?.name ?? "Error"}: ${e?.message ?? "unknown"}`,
      e?.stack ?? "",
      "Component stack:",
      this.state.where,
    ].join("\n");
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className={cx("pane")}>
        <section className={cx("card")} role="alert">
          <h2 className={cx("h")}>Mesh Messenger couldn’t draw this panel</h2>
          <p className={cx("help")}>
            Something in the feature failed, and it has been contained here — the rest of Zeus is unaffected, and your
            nodes keep running and collecting messages. Switching tabs or reopening the feature usually clears it.
          </p>
          <p className={`${cx("help")} ${cx("mono")}`}>
            {this.state.error.name}: {this.state.error.message}
          </p>
          <div className={cx("row")}>
            <button
              type="button"
              className={`${cx("button")} ${cx("button--primary")}`}
              onClick={() => this.setState({ error: null, where: "" })}
            >
              Try again
            </button>
            <button
              type="button"
              className={cx("button")}
              onClick={() => void navigator.clipboard?.writeText(this.report()).catch(() => {})}
            >
              Copy the details
            </button>
          </div>
          <p className={cx("help")}>Please send those details with a bug report; they say exactly where it went wrong.</p>
        </section>
      </div>
    );
  }
}
