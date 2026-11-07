# Swipe Maze

An Android maze game. Swipe to run your avatar through the maze from the opening at the top (Start) to the opening at the bottom (Finish). Every maze is generated fresh and winds about instead of heading straight for the finish.

## How to play

- **Move:** swipe up, down, left or right. The avatar runs along the corridor, round the bends, until it reaches a fork, a dead end or the finish, so a long corridor is one swipe. At a fork, swipe the way you want to go.
- **Back up:** swipe back along your trail — it runs back to the nearest fork (or the start). Or tap any square on your trail (it asks first) to jump straight back to it. The trail after that square is erased.
- **Retry:** starts the same maze over, as often as you like (it asks first, since a trail can be long).
- **Zoom:** pinch with two fingers to zoom, drag with two fingers to pan. The view follows your avatar, and **Fit** shows the whole maze again.
- **Sounds:** a whoosh when you swipe (higher or lower depending on the direction), a softer blip when you back up, a buzz when you hit a wall, and a jingle with fireworks when you solve a maze. Sound follows your phone's media volume.
- **Levels:** 100 levels, one maze each. Level 1 is 10×10 and every level adds one square per side, up to 109×109 at level 100.
- **Progress:** saved under your name. Come back and pick **Continue** or **Start over**. The leaderboard shows the furthest level each player has reached.

## Install

Download the APK from the [latest release](../../releases/latest), open it on your Android phone and allow "install unknown apps" when asked. Needs Android 6.0 or newer.

The app asks for no permissions. Your name and level are stored only on your phone.

## Build

```
SwipeMaze.Core/    Maze generation, game rules, leaderboard (no platform code, unit tested)
SwipeMaze/         .NET MAUI Android app
SwipeMaze.Tests/   xUnit tests
```

```bash
dotnet test SwipeMaze.Tests
dotnet build SwipeMaze/SwipeMaze.csproj -c Release   # needs the .NET MAUI Android workload, Android SDK and JDK 17
```

Release builds skip ahead-of-time compilation (see `SwipeMaze.csproj`), so they don't need the extra Android cross-compiler workload packs. Sign release builds with your own keystore via the `AndroidKeyStore` / `AndroidSigningKeyStore` properties. Don't build the `.sln` with `-f net10.0-android`.

## License

MIT
