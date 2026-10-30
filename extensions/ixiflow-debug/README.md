# IxIFlow Debugger for VS Code

Attach VS Code to the Debug Adapter Protocol endpoint hosted by an IxIFlow application. Supports attach and launch, source mapping, breakpoints, stepping, inspection and recorded replay.

Open the repository root in VS Code and choose **Run > Start Debugging > Extension** to launch an Extension Development Host. The repository's checked-in `.vscode/launch.json` uses the JavaScript source directly; no build or dependency installation is required. Use Node.js 18 or later for local tests:

```sh
npm test
```

Enable debugging and authorize the adapter endpoint in your application, then configure an `ixiflow` attach or launch session in the development window. See [the debug adapter guide](https://github.com/HumanHub/IxIFlow/blob/main/docs/debug-adapter.md) for pipe/TCP endpoints, token inputs and source mappings.

The extension uses the `humanhub` publisher placeholder; marketplace ownership and publication are maintained separately. Licensed under [Apache 2.0](LICENSE).
