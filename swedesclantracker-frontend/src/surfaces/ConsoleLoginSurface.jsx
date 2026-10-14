import { useState } from "react";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { BeveledButton } from "../components/osrs/BeveledButton";

export function ConsoleLoginSurface({ returnTo, onSubmit, error, submitting }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  return <main className="auth-shell"><section className="auth-card"><div className="auth-mark"><IconGlyph name="crest" size={34} /></div><span className="eyebrow">SWEDES CLAN TRACKER</span><h1>Operator sign-in</h1><p className="auth-copy">Authenticate to open the Watchtower Brief and read-only operational records.</p><form onSubmit={(event) => { event.preventDefault(); onSubmit(username, password); }}><label className="field"><span>Username</span><input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="username" required /></label><label className="field"><span>Password</span><input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" required /></label>{error ? <div className="form-error" role="alert"><IconGlyph name="alert" size={15} />{error}</div> : null}<BeveledButton type="submit" variant="primary" loading={submitting} icon="shield">Sign in</BeveledButton></form>{returnTo && returnTo !== "/" ? <small className="return-note">After sign-in you will return to the requested route.</small> : null}</section></main>;
}

export function ConsoleNotFoundSurface({ onNavigate }) {
  return <PageFrameFallback onNavigate={onNavigate} />;
}

function PageFrameFallback({ onNavigate }) {
  return <main className="not-found-shell"><div className="not-found-card"><span className="eyebrow">404 · ROUTE NOT FOUND</span><h1>This route is outside the watchtower.</h1><p>Use the approved navigation routes to return to the operator console.</p><button type="button" className="osrs-button osrs-button-primary" onClick={() => onNavigate("/")}><IconGlyph name="arrow-left" size={15} />Back to overview</button></div></main>;
}
