# Flutter widget preview sample

Minimal Flutter package for Vistudio's widget preview (`flutter widget-preview`, Flutter 3.35+):

- `lib/widgets/greeting_card.dart`: a `StatelessWidget` with two `@Preview` functions (size, brightness, text scale).
- `lib/widgets/status_badge.dart`: a `StatefulWidget` without `@Preview` (previewed through a temporary wrapper project, or given one with the quick fix).

The live integration test (`Vistudio.Tests.exe --flutter-preview`) runs on a copy of this folder.
