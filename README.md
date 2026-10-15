# QuotaDeck

AI-provider quota faces for the Logitech MX Creative Keypad.

QuotaDeck is a C# plugin for the Logi Actions SDK. Each of the keypad's nine
LCD keys can show one provider's usage: a provider-coloured ring with the
percentage inside, the weekly window as a secondary figure, and the time until
the window resets in the corner. When a tracked limit is hit, the whole panel
pulses red.

Readers follow [Codenotch](https://github.com/vinzdg/codenotch) provider by
provider: the same credential sources, the same endpoints, the same
error-to-status mapping. A reader that cannot answer says so (`stale`,
`needsAuth`, `error`, ...) and never invents a number.

Providers: Claude, Codex, OpenCode Go, Antigravity, Cursor, Muse, Devin.

Status: early development. macOS first; Windows later.

## License

MIT. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
