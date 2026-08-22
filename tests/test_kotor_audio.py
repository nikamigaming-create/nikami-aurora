"""Synthetic tests for KOTOR audio normalization without retail data."""

from __future__ import annotations

import struct
import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

from kotor_audio import normalize_wav_for_godot  # noqa: E402


def wave_payload(
    format_tag: int,
    channels: int,
    sample_rate: int,
    block_align: int,
    bits_per_sample: int,
    payload: bytes,
) -> bytes:
    byte_rate = sample_rate * block_align
    fmt = struct.pack(
        "<HHIIHH", format_tag, channels, sample_rate, byte_rate,
        block_align, bits_per_sample)
    body = b"WAVEfmt " + struct.pack("<I", len(fmt)) + fmt
    body += b"data" + struct.pack("<I", len(payload)) + payload
    return b"RIFF" + struct.pack("<I", len(body)) + body


class KotorAudioTests(unittest.TestCase):
    def test_mono_ima_adpcm_decodes_to_pcm16(self) -> None:
        block = struct.pack("<hBB", 0, 0, 0) + b"\x00" * 4
        source = wave_payload(0x11, 1, 8000, len(block), 4, block)

        playable, source_encoding, payload_encoding = normalize_wav_for_godot(
            source, "synthetic_blaster")

        self.assertEqual("ima-adpcm-wav", source_encoding)
        self.assertEqual("pcm-s16le-wav", payload_encoding)
        self.assertEqual(b"RIFF", playable[:4])
        self.assertEqual(1, struct.unpack_from("<H", playable, 20)[0])
        self.assertEqual(16, struct.unpack_from("<H", playable, 34)[0])
        self.assertEqual(18, struct.unpack_from("<I", playable, 40)[0])
        self.assertEqual(b"\x00" * 18, playable[44:])

    def test_pcm16_passes_through_byte_for_byte(self) -> None:
        source = wave_payload(1, 1, 22050, 2, 16, b"\x01\x00\xff\xff")

        playable, source_encoding, payload_encoding = normalize_wav_for_godot(
            source, "synthetic_impact")

        self.assertIs(source, playable)
        self.assertEqual("pcm-s16le-wav", source_encoding)
        self.assertEqual("pcm-s16le-wav", payload_encoding)

    def test_unsupported_stereo_ima_fails_closed(self) -> None:
        block = struct.pack("<hBB", 0, 0, 0) + b"\x00" * 4
        source = wave_payload(0x11, 2, 8000, len(block), 4, block)

        with self.assertRaisesRegex(RuntimeError, "Unsupported IMA ADPCM layout"):
            normalize_wav_for_godot(source, "synthetic_stereo")


if __name__ == "__main__":
    unittest.main()
