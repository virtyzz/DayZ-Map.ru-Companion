# Tesseract OCR assets

These checked-in files are bundled by `installer/Crosslay.iss` and verified by
Inno Setup before installation.

| File | Version/source | SHA-256 |
| --- | --- | --- |
| `tesseract-ocr-w64-setup-5.5.3.20260724.exe` | [Tesseract OCR 5.5.3 Windows release](https://github.com/tesseract-ocr/tesseract/releases/tag/5.5.3) | `bee9e3434bd94fd65387d9be28cd467a41f61b1275383b55b0f59a1331270ae4` |
| `eng.traineddata` | [tessdata_fast / eng](https://github.com/tesseract-ocr/tessdata_fast/blob/main/eng.traineddata) | `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` |
| `rus.traineddata` | [tessdata_fast / rus](https://github.com/tesseract-ocr/tessdata_fast/blob/main/rus.traineddata) | `e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225` |

The Tesseract installer is placed in the current user's `LocalAppData` during
Companion setup. Both language models are copied to its `tessdata` directory.
