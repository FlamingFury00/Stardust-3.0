"""Evaluation-tool tests. Synthetic fixtures are not Rocket League match evidence."""
import csv
import json
from pathlib import Path
import random
import shutil
import tempfile
import unittest

import blind_review as review


class BlindReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.public, self.key = self.root / "public", self.root / "private" / "answer.json"
        self.manifest = self.root / "manifest.json"
        pairs = []
        for i in range(4):
            for side in ("candidate", "reference"):
                (self.root / f"{side}-{i}.mp4").write_bytes(f"synthetic-{side}-{i}".encode())
            pairs.append(dict(scenario=f"private-scenario-{i}", candidate=f"candidate-{i}.mp4",
                reference=f"reference-{i}.mp4", reference_kind="synthetic-test-only",
                provenance="unit fixture; not gameplay", candidate_revision="test-only", seconds=1))
        self.spec = dict(blinding_checked=True, pairs=pairs)
        self.write_spec()

    def write_spec(self):
        self.manifest.write_text(json.dumps(self.spec), encoding="utf-8")

    @staticmethod
    def encoder(source, target, seconds):
        target.write_bytes(b"normalized-fixture:" + source.read_bytes())

    def build(self, **kwargs):
        return review.build(self.manifest, self.public, self.key, rng=random.Random(517),
                            encoder=kwargs.get("encoder", self.encoder))

    def ratings(self, winners=None):
        answer = json.loads(self.key.read_text())
        rows = []
        for i, case in enumerate(answer["cases"]):
            candidate = case["candidate_side"]
            reference = "B" if candidate == "A" else "A"
            winner = (winners or ["candidate"] * 4)[i]
            row = dict(case_id=case["case_id"], winner={"candidate": candidate, "reference": reference, "tie": "TIE"}[winner])
            for dimension in review.DIMENSIONS:
                row[f"{dimension}_{candidate}"] = 5
                row[f"{dimension}_{reference}"] = 1
            rows.append(row)
        self.write_ratings(rows)
        return rows

    def write_ratings(self, rows):
        with (self.public / "ratings.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=review.COLUMNS)
            writer.writeheader()
            writer.writerows(rows)

    def test_public_bundle_has_no_identity_or_key(self):
        self.assertEqual(self.build(), 4)
        self.assertEqual(len(list(self.public.glob("*.mp4"))), 8)
        for path in (self.public / "index.html", self.public / "ratings.csv"):
            text = path.read_text()
            for secret in ("candidate-", "reference-", "private-scenario", "answer.json", "test-only"):
                self.assertNotIn(secret, text)
        self.assertNotIn(self.public, self.key.parents)
        self.assertTrue(self.key.is_file())

    def test_key_cannot_be_inside_public(self):
        with self.assertRaises(ValueError):
            review.build(self.manifest, self.public, self.public / "key.json", encoder=self.encoder)
        self.assertFalse(self.public.exists())

    def test_existing_review_is_not_overwritten(self):
        self.build()
        original = self.key.read_bytes()
        with self.assertRaises(ValueError):
            self.build()
        self.assertEqual(self.key.read_bytes(), original)

    def test_visible_identity_check_is_required(self):
        self.spec["blinding_checked"] = False
        self.write_spec()
        with self.assertRaises(ValueError):
            self.build()

    def test_same_clip_under_different_names_is_rejected(self):
        shutil.copyfile(self.root / "candidate-0.mp4", self.root / "reference-0.mp4")
        with self.assertRaises(ValueError):
            self.build()

    def test_invalid_duration_and_missing_provenance_are_rejected(self):
        for value in (float("nan"), 0, -1, 121):
            self.spec["pairs"][0]["seconds"] = value
            self.write_spec()
            with self.assertRaises(ValueError):
                self.build()
        self.spec["pairs"][0]["seconds"] = 1
        del self.spec["pairs"][0]["provenance"]
        self.write_spec()
        with self.assertRaises(ValueError):
            self.build()

    def test_encoding_failure_cleans_staging_without_publishing(self):
        def fail(*args):
            raise ValueError("synthetic encoder failure")
        with self.assertRaises(ValueError):
            self.build(encoder=fail)
        self.assertFalse(self.public.exists())
        self.assertFalse(self.key.exists())
        self.assertEqual(list(self.root.glob(".review-*")), [])

    def test_partial_review_is_rejected(self):
        self.build()
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")
        rows = self.ratings()
        self.write_ratings(rows[:-1])
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")

    def test_duplicate_and_unknown_cases_are_rejected(self):
        self.build()
        rows = self.ratings()
        self.write_ratings(rows + [rows[0]])
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")
        rows[0]["case_id"] = "case-999"
        self.write_ratings(rows)
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")

    def test_out_of_range_score_and_invalid_winner_are_rejected(self):
        self.build()
        rows = self.ratings()
        rows[0]["coverage_A"] = 6
        self.write_ratings(rows)
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")
        rows = self.ratings()
        rows[0]["winner"] = "candidate"
        self.write_ratings(rows)
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")

    def test_scoring_decodes_sides_and_reports_uncertainty(self):
        self.build()
        self.ratings(["candidate", "candidate", "reference", "candidate"])
        result = review.score(self.key, self.public / "ratings.csv")
        self.assertEqual((result["candidate_wins"], result["reference_wins"], result["ties"]), (3, 1, 0))
        self.assertEqual(result["decisive_preference_fraction"], 0.75)
        self.assertAlmostEqual(result["two_sided_sign_test_p"], 0.625)
        self.assertLess(result["wilson_95_decisive"][0], 0.75)
        self.assertGreater(result["wilson_95_decisive"][1], 0.75)
        self.assertEqual(result["mean_candidate_minus_reference"]["coverage"], 4)
        self.assertEqual(len(result["ratings_sha256"]), 64)

    def test_all_ties_do_not_fabricate_a_preference(self):
        self.build()
        self.ratings(["tie"] * 4)
        result = review.score(self.key, self.public / "ratings.csv")
        self.assertEqual(result["ties"], 4)
        self.assertIsNone(result["decisive_preference_fraction"])
        self.assertIsNone(result["wilson_95_decisive"])
        self.assertIsNone(result["two_sided_sign_test_p"])

    def test_changed_video_is_detected_before_scoring(self):
        self.build()
        self.ratings()
        next(self.public.glob("*.mp4")).write_bytes(b"replaced video")
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")

    def test_malformed_json_roots_are_rejected(self):
        self.manifest.write_text("[]", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.build()
        self.write_spec()
        self.build()
        self.ratings()
        self.key.write_text("[]", encoding="utf-8")
        with self.assertRaises(ValueError):
            review.score(self.key, self.public / "ratings.csv")

    def test_short_and_overwide_csv_rows_are_rejected(self):
        self.build()
        ratings = self.public / "ratings.csv"
        for row in ("case-001,A", "case-001,A,5,5,5,5,5,5,note,extra"):
            ratings.write_text(",".join(review.COLUMNS) + "\n" + row + "\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                review.score(self.key, ratings)

    def test_source_changes_during_encoding_are_rejected(self):
        def mutate(source, target, seconds):
            self.encoder(source, target, seconds)
            source.write_bytes(b"changed while encoding")
        with self.assertRaises(ValueError):
            self.build(encoder=mutate)
        self.assertFalse(self.public.exists())
        self.assertFalse(self.key.exists())

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg not installed")
    def test_real_normalization_removes_audio_and_identity_metadata(self):
        source, target = self.root / "source.mp4", self.root / "normalized.mp4"
        review.command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi",
            "-i", "color=c=black:s=160x90:r=30:d=0.3", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.3",
            "-metadata", "title=PRIVATE_PLAYER_NAME", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", str(source)])
        review.normalize(source, target, 0.2)
        probe = json.loads(review.command(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(target)]))
        self.assertEqual(len(probe["streams"]), 1)
        video = probe["streams"][0]
        self.assertEqual((video["width"], video["height"], video["r_frame_rate"]), (1280, 720, "60/1"))
        self.assertNotIn("PRIVATE_PLAYER_NAME", json.dumps(probe))
        with self.assertRaises(ValueError):
            review.normalize(source, self.root / "too-long.mp4", 5)


if __name__ == "__main__":
    unittest.main()
