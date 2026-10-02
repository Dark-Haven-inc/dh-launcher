# Fonts

Bundled so the launcher needs nothing installed or downloaded. All SIL Open Font License 1.1.

| file | source |
|------|--------|
| `Inter-Regular.ttf`, `Inter-Medium.ttf`, `Inter-SemiBold.ttf` | Inter 4.1 ([rsms/inter](https://github.com/rsms/inter)), static instances from `Inter.ttc`: the legacy theme's text. `Inter-OFL.txt`. |
| `Alegreya-Regular.ttf`, `Alegreya-Medium.ttf`, `Alegreya-Bold.ttf` | Alegreya 2.x ([huertatipografica/Alegreya](https://github.com/huertatipografica/Alegreya), via google/fonts), static instances of the variable font (fontTools `instancer`, names set like Inter's): the medieval theme's text. `Alegreya-OFL.txt`. |
| `AlegreyaSC-Regular.ttf`, `AlegreyaSC-Bold.ttf` | Alegreya SC, as published: the medieval theme's headings and labels. `AlegreyaSC-OFL.txt`. |
| `Tektur-Medium.ttf`, `Tektur-Bold.ttf` | Tektur ([hyvyys/Tektur](https://github.com/hyvyys/Tektur)), static instances (width 100): the cyberpunk theme's headings, labels and navigation. `Tektur-OFL.txt`. |
| `Exo2-Regular.ttf`, `Exo2-Medium.ttf`, `Exo2-SemiBold.ttf` | Exo 2 ([googlefonts/Exo-2.0](https://github.com/googlefonts/Exo-2.0)), static instances: the cyberpunk theme's text. `Exo2-OFL.txt`. |
| `JetBrainsMonoNerdFontPropo-Regular.ttf`, `JetBrainsMonoNerdFontPropo-Medium.ttf` | JetBrains Mono 2.304 patched by [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts) 3.5.1, the "Propo" build: text keeps its fixed width, icons get their own (the plain build squeezes them into one cell and they overflow it, which Avalonia clips). `JetBrainsMonoNerdFont-OFL.txt`. |

The Nerd Fonts icons come from their own projects under their own licenses (Codicons CC BY 4.0, Material
Design Icons Apache 2.0, Font Awesome OFL 1.1, …) — see the Nerd Fonts repository. The launcher uses its
icons as text glyphs of the mono font, e.g. `&#xEAA2;` (`cod-bell`).
