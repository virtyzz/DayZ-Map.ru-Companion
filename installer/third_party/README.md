# Tesseract OCR assets

These checked-in files are bundled by `installer/Crosslay.iss` and verified by
Inno Setup before installation.

| File | Version/source | SHA-256 |
| --- | --- | --- |
| `tesseract-ocr-w64-setup-5.5.0.20241111.exe` | [Tesseract OCR 5.5.0 Windows release](https://github.com/tesseract-ocr/tesseract/releases/tag/5.5.0) | `f3fc4236425b690c8be756f35793f77394ee004be0a6460a440c754d892f68bc` |
| `rus.traineddata` | [tessdata_fast / rus](https://github.com/tesseract-ocr/tessdata_fast/blob/main/rus.traineddata) | `e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225` |

The Tesseract installer is placed in the current user's `LocalAppData` during
Companion setup. The Russian model is copied to its `tessdata` directory; the
installer supplies English.
