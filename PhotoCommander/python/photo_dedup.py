#!/usr/bin/env python3
"""
Поиск одинаковых / похожих фото и сортировка по папкам с помощью OpenCV.

Установка:
    pip install opencv-python numpy
    pip install pillow        # необязательно: для даты съёмки из EXIF

Примеры:
    # только отчёт, ничего не трогает
    python photo_dedup.py "D:/Фото"

    # переместить дубликаты в отдельную папку (лучшая копия остаётся на месте)
    python photo_dedup.py "D:/Фото" --move-dups

    # разложить уникальные фото по папкам ГГГГ/ГГГГ-ММ (копированием)
    python photo_dedup.py "D:/Фото" --sort-to "D:/Отсортировано"

    # строже/мягче сравнение (0 = почти идентичные, 10-12 = похожие кадры)
    python photo_dedup.py "D:/Фото" --threshold 4

    # фото, снятые в пределах 30 секунд, сравнивать с порогом 18 (серии кадров)
    python photo_dedup.py "D:/Фото" --time-window 30 --time-threshold 18
"""
import argparse
import csv
import hashlib
import shutil
import sys
from datetime import datetime
from pathlib import Path

import cv2
import numpy as np

IMG_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"}


# ---------- чтение (работает и с кириллицей в путях на Windows) ----------
def imread_any(path: Path):
    data = np.fromfile(str(path), dtype=np.uint8)
    if data.size == 0:
        return None
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def md5(path: Path, chunk=1 << 20) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(chunk), b""):
            h.update(block)
    return h.hexdigest()


# ---------- перцептивный хэш (pHash на DCT, 64 бита) ----------
def phash(img) -> int:
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    small = cv2.resize(gray, (32, 32), interpolation=cv2.INTER_AREA).astype(np.float32)
    dct = cv2.dct(small)[:8, :8]
    med = np.median(dct.flatten()[1:])  # без DC-компоненты
    bits = (dct > med).flatten()
    return int("".join("1" if b else "0" for b in bits), 2)


# ---------- дата съёмки ----------
def exif_date(path: Path):
    """Дата съёмки из EXIF или None."""
    try:
        from PIL import Image
        with Image.open(path) as im:
            exif = im.getexif()
            raw = exif.get_ifd(0x8769).get(36867) or exif.get(306)  # DateTimeOriginal / DateTime
            if raw:
                return datetime.strptime(str(raw).strip()[:19], "%Y:%m:%d %H:%M:%S")
    except Exception:
        pass
    return None


def photo_date(path: Path) -> datetime:
    return exif_date(path) or datetime.fromtimestamp(path.stat().st_mtime)


# ---------- union-find для групп ----------
class DSU:
    def __init__(self, n):
        self.p = list(range(n))

    def find(self, x):
        while self.p[x] != x:
            self.p[x] = self.p[self.p[x]]
            x = self.p[x]
        return x

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[rb] = ra


def popcount64(arr: np.ndarray) -> np.ndarray:
    return np.unpackbits(arr.view(np.uint8).reshape(-1, 8), axis=1).sum(axis=1)


def main():
    ap = argparse.ArgumentParser(description="Поиск дубликатов фото через OpenCV")
    ap.add_argument("folder", type=Path)
    ap.add_argument("--threshold", type=int, default=6,
                    help="макс. расстояние Хэмминга между хэшами (по умолчанию 6)")
    ap.add_argument("--time-window", type=float, default=10,
                    help="фото, снятые с разницей не больше N секунд, сравниваются "
                         "мягче (по умолчанию 10; 0 = выключить)")
    ap.add_argument("--time-threshold", type=int, default=14,
                    help="порог для близких по времени фото (по умолчанию 14)")
    ap.add_argument("--time-use-mtime", action="store_true",
                    help="если нет EXIF, брать время изменения файла "
                         "(осторожно: у скопированных файлов оно может совпадать)")
    ap.add_argument("--move-dups", action="store_true",
                    help="переместить дубликаты в <папка>/_duplicates")
    ap.add_argument("--sort-to", type=Path,
                    help="скопировать уникальные фото в папки ГГГГ/ГГГГ-ММ")
    ap.add_argument("--report", type=Path, default=Path("duplicates_report.csv"))
    args = ap.parse_args()

    dup_dir = args.folder / "_duplicates"
    files = sorted(p for p in args.folder.rglob("*")
                   if p.suffix.lower() in IMG_EXT and dup_dir not in p.parents)
    print(f"Найдено изображений: {len(files)}")

    items = []  # (path, md5, phash, pixels, size)
    times = []  # время съёмки в секундах или None
    for i, p in enumerate(files, 1):
        img = imread_any(p)
        if img is None:
            print(f"  пропуск (не читается): {p}")
            continue
        h, w = img.shape[:2]
        items.append((p, md5(p), phash(img), h * w, p.stat().st_size))
        dt = exif_date(p)
        if dt is None and args.time_use_mtime:
            dt = datetime.fromtimestamp(p.stat().st_mtime)
        times.append(dt.timestamp() if dt else None)
        if i % 200 == 0:
            print(f"  обработано {i}/{len(files)}")

    n = len(items)
    if n == 0:
        return
    dsu = DSU(n)
    reason = {}  # индекс -> почему попал в группу

    # 1) точные копии по MD5
    seen = {}
    for i, it in enumerate(items):
        if it[1] in seen:
            dsu.union(seen[it[1]], i)
            reason[i] = "копия файла"
        else:
            seen[it[1]] = i

    # 2) похожие по pHash (векторно, по строкам)
    hashes = np.array([it[2] for it in items], dtype=np.uint64)
    for i in range(n - 1):
        dist = popcount64(hashes[i + 1:] ^ hashes[i])
        for j in np.nonzero(dist <= args.threshold)[0]:
            k = i + 1 + int(j)
            dsu.union(i, k)
            reason.setdefault(k, "похожее изображение")
            reason.setdefault(i, "похожее изображение")

    # 3) снятые почти одновременно — сравниваем мягче
    close_pairs = 0
    if args.time_window > 0:
        timed = sorted((t, i) for i, t in enumerate(times) if t is not None)
        for a in range(len(timed)):
            ta, i = timed[a]
            for b in range(a + 1, len(timed)):
                tb, k = timed[b]
                if tb - ta > args.time_window:
                    break
                d = bin(int(items[i][2]) ^ int(items[k][2])).count("1")
                if d <= args.time_threshold:
                    if dsu.find(i) != dsu.find(k):
                        close_pairs += 1
                    dsu.union(i, k)
                    reason.setdefault(i, f"снято рядом (≤{args.time_window:g} с)")
                    reason.setdefault(k, f"снято рядом (≤{args.time_window:g} с)")
        print(f"С датой съёмки: {len(timed)} из {n}; "
              f"доп. совпадений по времени: {close_pairs}")

    groups = {}
    for i in range(n):
        groups.setdefault(dsu.find(i), []).append(i)

    # в каждой группе оставляем лучшую: больше пикселей, затем больше файл
    keep, dups = [], []
    rows = []
    gid = 0
    for members in groups.values():
        members.sort(key=lambda k: (items[k][3], items[k][4]), reverse=True)
        best = members[0]
        keep.append(best)
        if len(members) > 1:
            gid += 1
            for k in members:
                d = bin(int(items[k][2]) ^ int(items[best][2])).count("1")
                ts = (datetime.fromtimestamp(times[k]).strftime("%Y-%m-%d %H:%M:%S")
                      if times[k] is not None else "")
                rows.append([gid, "KEEP" if k == best else "DUP",
                             str(items[k][0]), items[k][3], items[k][4], d, ts,
                             reason.get(k, "")])
                if k != best:
                    dups.append(k)

    with open(args.report, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=";")
        w.writerow(["группа", "статус", "файл", "пикселей", "байт", "расстояние",
                    "время съёмки", "причина"])
        w.writerows(rows)

    print(f"Групп дубликатов: {gid}, лишних копий: {len(dups)}")
    print(f"Отчёт: {args.report.resolve()}")

    if args.move_dups and dups:
        dup_dir.mkdir(exist_ok=True)
        for k in dups:
            src = items[k][0]
            dst = dup_dir / src.relative_to(args.folder)
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(src), str(dst))
        print(f"Дубликаты перемещены в {dup_dir}")

    if args.sort_to:
        for k in keep:
            src = items[k][0]
            dt = photo_date(src)
            target = args.sort_to / f"{dt:%Y}" / f"{dt:%Y-%m}"
            target.mkdir(parents=True, exist_ok=True)
            dst = target / src.name
            c = 1
            while dst.exists():
                dst = target / f"{src.stem}_{c}{src.suffix}"
                c += 1
            shutil.copy2(src, dst)
        print(f"Уникальные фото разложены по датам в {args.sort_to}")


if __name__ == "__main__":
    sys.exit(main())
