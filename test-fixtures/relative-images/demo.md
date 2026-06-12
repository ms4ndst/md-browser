# Relative image rendering test

This file lives next to `images/sample.png`. If the viewer renders local
images correctly, you should see a Mauve box with "Relative OK" written
on it below.

![relative](images/sample.png)

That image is loaded via the `mdbrowser.local` virtual host. WebView2
otherwise refuses `file://` reads from `about:blank` documents.
