# Third-party components

The Store package includes Tesseract OCR and English/Russian language models.
Tesseract's license, authors and README are included under `ocr/doc`.

- Tesseract OCR: https://github.com/tesseract-ocr/tesseract (Apache-2.0).
- Language models: https://github.com/tesseract-ocr/tessdata_fast (Apache-2.0).
- Windows OCR distribution and runtime dependency information:
  https://github.com/UB-Mannheim/tesseract/wiki.

Tesseract's DLL dependencies have their own licenses; the Tesseract license does
not replace them. Before public distribution, the publisher must collect the
notices and any corresponding-source material required by the exact Windows
runtime distribution used for the build. The build copies the runtime's supplied
`doc` directory; that alone is not a certification of dependency-license compliance.

The package also includes the Microsoft .NET runtime and WebView2 SDK files,
subject to their respective license terms and included notices.
