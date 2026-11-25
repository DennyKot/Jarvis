# Avatar animations (optional)

Put Lottie JSON animations here to replace the built-in robot character.
File names match the assistant states:

`idle.json`, `listening.json`, `transcribing.json`, `thinking.json`, `acting.json`, `confirming.json`, `speaking.json`, `error.json`

`idle.json` is enough to switch to Lottie; states without their own file reuse it.
Files are copied to the output folder on build.
