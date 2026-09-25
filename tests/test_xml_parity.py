from __future__ import annotations

import json
from pathlib import Path

from build_truth_dataset import parse_generic_xml


FIXTURE_PATH = (
    Path(__file__).parent
    / "InvoiceFlowAI.Infrastructure.Tests"
    / "Fixtures"
    / "DesktopParity"
    / "xml-extraction.json"
)


def test_shared_xml_extraction_fixtures_match_python_projection(tmp_path):
    fixtures = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))["cases"]
    for fixture in fixtures:
        source = tmp_path / f"{fixture['caseId']}.xml"
        source.write_text(fixture["xml"], encoding="utf-8")
        actual = parse_generic_xml(source)
        expected = fixture["expected"]
        assert actual["invoice_number"] == expected["invoiceNumber"], fixture["caseId"]
        assert actual["invoice_date"] == expected["invoiceDate"], fixture["caseId"]
        assert actual["purchaser"] == expected["purchaser"], fixture["caseId"]
        assert actual["seller"] == expected["seller"], fixture["caseId"]
        assert actual["amount"] == expected["amount"], fixture["caseId"]
