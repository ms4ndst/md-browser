# Base64 image rendering test

This file embeds a tiny 8×8 red square as a base64 PNG. If you see a red square below, base64 images render fine.

![red square](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAFklEQVQYV2P8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==)

A second, larger inline (32×32 lavender):

![lavender square](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAJUlEQVRIx2NkoBAwjhowasCoAaMGjBowasCoAaMGjBowtA0AAALAAAEUL+0OAAAAAElFTkSuQmCC)

The originals in `ORGINAL_Redovisning_ControlEdge.md` look like this instead:

```
![](data:image/png;base64...)
```

Three dots — no payload — nothing to decode.
