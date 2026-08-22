"""Dependency-free KOTOR audio normalization used by the owned-data importer."""

from __future__ import annotations

import struct


IMA_ADPCM_INDEX_TABLE = (-1, -1, -1, -1, 2, 4, 6, 8)
IMA_ADPCM_STEP_TABLE = (
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
    143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449,
    494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
    1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
    4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
    11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623,
    27086, 29794, 32767,
)


def riff_wave_chunks(data: bytes, resref: str) -> dict[bytes, bytes]:
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise RuntimeError(f"Encounter audio is not RIFF/WAVE: {resref}")
    declared_size = struct.unpack_from("<I", data, 4)[0] + 8
    if declared_size > len(data):
        raise RuntimeError(f"Encounter WAV is truncated: {resref}")
    chunks: dict[bytes, bytes] = {}
    offset = 12
    while offset + 8 <= declared_size:
        chunk_id = data[offset:offset + 4]
        chunk_size = struct.unpack_from("<I", data, offset + 4)[0]
        chunk_start = offset + 8
        chunk_end = chunk_start + chunk_size
        if chunk_end > declared_size:
            raise RuntimeError(f"Encounter WAV chunk is truncated: {resref}")
        chunks.setdefault(chunk_id, data[chunk_start:chunk_end])
        offset = chunk_end + (chunk_size & 1)
    return chunks


def decode_mono_ima_adpcm_wav(data: bytes, resref: str) -> bytes:
    chunks = riff_wave_chunks(data, resref)
    fmt = chunks.get(b"fmt ")
    encoded = chunks.get(b"data")
    if fmt is None or encoded is None or len(fmt) < 16:
        raise RuntimeError(f"Encounter IMA ADPCM WAV is missing fmt/data: {resref}")
    format_tag, channels, sample_rate, _, block_align, bits_per_sample = (
        struct.unpack_from("<HHIIHH", fmt)
    )
    if (format_tag != 0x11 or channels != 1 or bits_per_sample != 4 or
            block_align < 5 or len(encoded) % block_align != 0):
        raise RuntimeError(
            f"Unsupported IMA ADPCM layout for {resref}: "
            f"tag={format_tag} channels={channels} bits={bits_per_sample} "
            f"blockAlign={block_align} dataBytes={len(encoded)}")

    samples: list[int] = []
    for block_offset in range(0, len(encoded), block_align):
        block = encoded[block_offset:block_offset + block_align]
        predictor, step_index, reserved = struct.unpack_from("<hBB", block)
        if step_index >= len(IMA_ADPCM_STEP_TABLE) or reserved != 0:
            raise RuntimeError(
                f"Invalid IMA ADPCM block header for {resref} at {block_offset}")
        samples.append(predictor)
        for packed in block[4:]:
            for nibble in (packed & 0x0F, packed >> 4):
                step = IMA_ADPCM_STEP_TABLE[step_index]
                difference = step >> 3
                if nibble & 1:
                    difference += step >> 2
                if nibble & 2:
                    difference += step >> 1
                if nibble & 4:
                    difference += step
                predictor += -difference if nibble & 8 else difference
                predictor = max(-32768, min(32767, predictor))
                step_index += IMA_ADPCM_INDEX_TABLE[nibble & 7]
                step_index = max(0, min(88, step_index))
                samples.append(predictor)

    pcm = bytearray(len(samples) * 2)
    for index, sample in enumerate(samples):
        struct.pack_into("<h", pcm, index * 2, sample)
    byte_rate = sample_rate * channels * 2
    pcm_header = (
        b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE" +
        b"fmt " + struct.pack(
            "<IHHIIHH", 16, 1, channels, sample_rate, byte_rate,
            channels * 2, 16) +
        b"data" + struct.pack("<I", len(pcm))
    )
    return pcm_header + pcm


def normalize_wav_for_godot(data: bytes, resref: str) -> tuple[bytes, str, str]:
    chunks = riff_wave_chunks(data, resref)
    fmt = chunks.get(b"fmt ")
    if fmt is None or len(fmt) < 16:
        raise RuntimeError(f"Encounter WAV has no usable fmt chunk: {resref}")
    format_tag, channels, sample_rate, _, block_align, bits_per_sample = (
        struct.unpack_from("<HHIIHH", fmt)
    )
    if format_tag == 1 and bits_per_sample in (8, 16):
        encoding = f"pcm-s{bits_per_sample}le-wav"
        return data, encoding, encoding
    if format_tag == 0x11:
        decoded = decode_mono_ima_adpcm_wav(data, resref)
        return decoded, "ima-adpcm-wav", "pcm-s16le-wav"
    raise RuntimeError(
        f"Unsupported encounter WAV codec for {resref}: tag={format_tag} "
        f"channels={channels} sampleRate={sample_rate} "
        f"blockAlign={block_align} bits={bits_per_sample}")
