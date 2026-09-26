import json
import sys
import unittest
from unittest.mock import patch

import pytest

from audit_email_truth import collect_truth_table, main


class AuditEmailTruthContractTests(unittest.TestCase):
    def test_collect_truth_table_returns_startup_contract_without_secrets(self):
        report = collect_truth_table(
            "invoice-user@example.com",
            "fixture-auth-code",
            "2025-11-25",
            "2026-06-14",
        )

        report_text = str(report)
        self.assertEqual(report["status"], "skipped")
        self.assertEqual(report["email_domain"], "example.com")
        self.assertEqual(report["date_from"], "2025-11-25")
        self.assertEqual(report["date_to"], "2026-06-14")
        self.assertNotIn("fixture-auth-code", report_text)
        self.assertNotIn("invoice-user@example.com", report_text)


def _valid_manifest(raw_path):
    return {
        "summary": {"finalized": True, "pending_review_count": 0},
        "included": [
            {
                "truth_type": "invoice",
                "document_role": "invoice",
                "invoice_date": "2026-06-01",
                "seller": "Synthetic Seller",
                "amount": "10.00",
                "source_email_id": "message-1",
                "file_name": "invoice.pdf",
                "raw_path": str(raw_path),
                "evidence": "synthetic fixture",
            }
        ],
        "excluded": [],
    }


def test_cli_accepts_a_finalized_truth_manifest(tmp_path, capsys):
    raw_file = tmp_path / "synthetic-source.bin"
    raw_file.write_bytes(b"synthetic")
    manifest_path = tmp_path / "truth-manifest.json"
    output_path = tmp_path / "audit-result.json"
    manifest_path.write_text(json.dumps(_valid_manifest(raw_file)), encoding="utf-8")

    with patch.object(sys, "argv", ["audit_email_truth.py", "--truth-manifest", str(manifest_path), "--output", str(output_path)]):
        main()

    result = json.loads(capsys.readouterr().out)
    assert result["finalized"] is True
    assert result["errors"] == []
    assert json.loads(output_path.read_text(encoding="utf-8")) == result


def test_cli_rejects_manifest_with_missing_required_field(tmp_path, capsys):
    raw_file = tmp_path / "synthetic-source.bin"
    raw_file.write_bytes(b"synthetic")
    manifest = _valid_manifest(raw_file)
    manifest["included"][0].pop("seller")
    manifest_path = tmp_path / "incomplete-truth-manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with patch.object(sys, "argv", ["audit_email_truth.py", "--truth-manifest", str(manifest_path)]):
        with pytest.raises(SystemExit) as exit_info:
            main()

    result = json.loads(capsys.readouterr().out)
    assert exit_info.value.code == 1
    assert result["finalized"] is False
    assert any(error["field"] == "seller" for error in result["errors"])


if __name__ == "__main__":
    unittest.main()
