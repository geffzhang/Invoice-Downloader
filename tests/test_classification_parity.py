import json
from pathlib import Path

from company_rules import classify_purchaser_relation
from document_types import apply_strong_train_evidence_override


FIXTURE_PATH = (
    Path(__file__).parent
    / "InvoiceFlowAI.Infrastructure.Tests"
    / "Fixtures"
    / "DesktopParity"
    / "classification.json"
)


def test_shared_classification_fixture_matches_python_and_csharp_expectations():
    fixture = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))

    for case in fixture["purchaserCases"]:
        relation = classify_purchaser_relation(case["purchaser"], case["companyName"])
        actual_disposition = {
            "unknown": "ManualReview",
            "non_target": "Retained",
            "target": "Accepted",
        }[relation]
        assert actual_disposition == case["expectedDisposition"], case["caseId"]
        if relation == "unknown":
            assert case["expectedReasonCode"] == "PURCHASER_UNKNOWN", case["caseId"]

    for case in fixture["trainClassificationCases"]:
        info_json = {
            "Departure_City": case["departureCity"],
            "Destination_City": case["destinationCity"],
        }
        metadata = {
            "subject": case["subject"],
            "attachment_name": case["attachmentName"],
            "original_filename": case["originalFileName"],
        }
        document_type, _ = apply_strong_train_evidence_override(
            "机票",
            "机票",
            case["seller"],
            info_json,
            metadata,
            case["fileName"],
            preview_loader=lambda: case["previewText"],
        )
        expected_type = "火车票" if case["expectedDocumentType"] == "TrainTicket" else "机票"
        assert document_type == expected_type, case["caseId"]