# Football Simulator Accessibility Mod — Roadmap

This document outlines planned features for future versions. The mod launched at v1.0 with full offensive accessibility. The roadmap is focused on closing the gap on defense and adding spatial awareness features that help blind players react in real time.

---

## Version 1.0 (Released)

- Full menu navigation
- Play selection — formations, play names, down and distance
- Pre-snap route reading — automatic at the line, L1 to repeat, re-reads after audibles
- Audibles — all three options announced with button labels
- Passing — open receivers called out by button name at snap and as they break open during the play
- On-demand status — R3 for full readout (quarter, time, score, down/distance, timeouts)
- Quick keyboard shortcuts — A for time remaining, S for score

**Known limitation:** Routes are announced on defense as well as offense. Defense accessibility is the primary focus of v1.1.

---

## Version 1.1 — Defensive Accessibility (Next)

The biggest gap in the current mod. A blind player on defense has no spatial awareness — no way to know where the ball carrier is, when to dive, or when a pass is coming their way.

### Ball Carrier Tracking
When the player is on defense and the ball is snapped, announce the direction and approximate distance from the controlled defender to the ball carrier. Update as the play develops.

> "Carrier, left, 8 yards."
> "Carrier, ahead, 3 yards."

Direction would be relative to the defender's facing — left, right, ahead, behind.

### Tackle Proximity Alert
When the controlled defender is within dive-tackle range of the ball carrier, announce a cue so the player knows to press X.

> "In range."

### Player Switch Feedback
When the player presses B to switch controlled defenders, announce who they just took control of and what that player's job is.

> "Switched to linebacker, right side."
> "Switched to safety."

### Pass Rush Feedback
For defensive linemen, announce when they have beaten their blocker and have a clear path to the QB.

> "QB unblocked."

### Defense Route Suppression
Silence the opponent's route announcements at pre-snap. Currently both sides hear routes — this should be filtered so only the offense hears their own play. (Requires identifying the correct field on FootballMatch that indicates which side the human controls.)

---

## Version 1.2 — Pass Defense and Interceptions

### Ball in the Air Alert
When a pass is thrown, announce that the ball is in the air and the general direction relative to the controlled defender.

> "Pass, right."
> "Pass, deep left."

### Interception Window
When a pass is in the air and the controlled defender has a realistic chance to intercept — ball trajectory brings it near the player — announce an interception cue.

> "Go for it."

This is a harder problem because it requires reading the ball's trajectory and comparing it to the defender's position and range.

### Fumble and Turnover Alerts
Announce fumbles and interceptions as they happen, including who recovered or who made the catch.

> "Fumble. Eagles recover."
> "Interception, Cowboys."

---

## Version 1.3 — Running Play Assists

### Hole Direction
On running plays, announce the intended gap direction when the ball is snapped, so the player knows which way to cut.

> "Run left." / "Run right." / "Run middle."

Most play names already imply direction (HB Toss = outside, FB Dive = middle), but an explicit spoken cue removes the need to memorize play names.

### Contact Alert
Announce when the ball carrier makes contact with a defender, giving the player a cue to press B (churn / fight for yards).

> "Contact."

### Yards After Contact Feedback
After the play ends, the post-play announcement already covers yards gained via down and distance changes. No additional feature needed here — covered by existing status readout.

---

## Version 1.4 — Quality of Life and Polish

### Configurable Verbosity
Let players choose how much they hear — minimal (open receivers only, no route reading), standard (current behavior), or detailed (position names, coverage type, etc.).

### Penalty Announcements
Announce penalties as they are called.

> "Flag. False start, offense."

### Timeout Tracking
Announce when a timeout is called and by whom. Currently timeouts are reflected in the R3 status readout but not announced the moment they occur.

### Halftime and End of Game
Announce halftime and final score with context.

> "Halftime. Eagles 21, Cowboys 14."
> "Final. Eagles 28, Cowboys 21. Eagles win."

---

## Long-Term / Under Consideration

- **Field goal and special teams** — kicking power/accuracy meter accessibility
- **Season and franchise mode** — standings readout, schedule, injury reports
- **Multiplayer** — if the game adds online play, ensure all accessibility features remain functional
- **Custom voice settings** — pitch, speed, NVDA voice profile recommendations

---

## Contributing / Feedback

If you are a blind player using this mod and something is missing or not working, please open an issue. Reports from actual users are the most valuable input for prioritizing this roadmap.
