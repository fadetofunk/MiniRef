# MiniRef

A Windows desktop tool for building [MiniMax H3](https://www.minimax.io/blog/minimax-h3) reference-to-video prompts and exporting them straight into a ready-to-run ComfyUI workflow.

MiniMax H3's ref2va (reference-to-video-and-audio) task can take a mix of reference images, videos, and audio clips and weave them into a generated scene — but writing the prompt by hand means tracking `<Picture N>`, `<Video N>`, `<Audio N>` tags, retention levels, and shot-by-shot descriptions across a fairly rigid six-section format. MiniRef gives you a form-based editor for that instead.

## What it does

- **Subjects, references, and shots** — define the people/objects/settings in your scene, attach picture/audio references and retention notes, and lay out shots with camera motion, timestamps, and descriptions.
- **Composed prompt** — automatically assembles the `subject_definitions` / `summary` / `retention_analysis` / `detailed_description` / `overall_soundscape` / `non_diegetic_music` prompt sections in the exact format MiniMax H3 expects, with reference tags numbered and cross-referenced consistently.
- **ComfyUI export** — takes the bundled `MiniMaxH3ReferenceToVideo` workflow template and rewires it for your project: swaps in `LoadImage`/`LoadAudio`/`VHS_LoadVideoPath` nodes for your actual references (each titled with its tag), wires in the composed prompt text, and lets you override which model files each loader node points at.
- **Prompt preview** — a standalone window to review the composed prompt before exporting.

## Project structure

- `src/MiniRef.Core` — prompt composition, reference numbering, ComfyUI workflow export, and settings/project persistence. No UI dependencies.
- `src/MiniRef.App` — WPF front end (MVVM via CommunityToolkit.Mvvm).
- `src/MiniRef.Core.Tests` — unit tests for the composer, exporter, and validator.

## Building and running

Requires the .NET 9 SDK and Windows (the app is WPF).

```bash
dotnet build
dotnet test
dotnet run --project src/MiniRef.App
```

On first run, point the app at your local ComfyUI installation via Settings — it's used to validate model paths and as the export target for generated workflows.

## License

GPL-3.0 — see [LICENSE](LICENSE).
