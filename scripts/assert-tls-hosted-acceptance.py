"""Verify exactly four transport facts plus their final guard and public certificate evidence."""
import base64
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

FACTS = (
    "DeclaredSecurity_OmittedServerIssuer_NoAutomaticAiaFetch",
    "ClientBundle_SuppliedIntermediate_IsPresentOnTls12Wire",
    "ClientBundle_OmittedIntermediate_RefusesOnTls12Wire",
    "ClientBundle_MismatchedIntermediate_RefusesOnTls12Wire",
)
PREFIX = "Vouchfx.Engine.Runtime.TransportAcceptance.Tests."
EXPECTED = {PREFIX + "HostedHttpTlsAcceptanceTests." + name for name in FACTS} | {
    PREFIX + "OwnedCertificateStoreGuardTests.FinalGuard_NoOwnedCertificateResidue"
}
KEYS = {"schemaVersion", "fact", "platform", "fixtureNonce", "scope", "rootDer", "issuerDer", "serverDer", "clientDer", "clientCertificateMessage", "serverCertificateMessage", "serverHelloVersion", "authenticated", "protocol", "failureType", "verdict", "decisions", "controls"}

def certificate_message(encoded):
    data = base64.b64decode(encoded, validate=True)
    assert len(data) <= 256 * 1024 and len(data) >= 7 and data[0] == 11
    assert int.from_bytes(data[1:4], "big") == len(data) - 4
    assert int.from_bytes(data[4:7], "big") == len(data) - 7
    result, offset = [], 7
    while offset < len(data):
        assert offset + 3 <= len(data)
        size = int.from_bytes(data[offset:offset+3], "big"); offset += 3
        assert 0 < size <= len(data) - offset
        result.append(data[offset:offset+size]); offset += size
    for certificate in result:
        der_certificate(base64.b64encode(certificate))
    return result

def der_certificate(encoded):
    data = base64.b64decode(encoded, validate=True)
    # Certificate ::= SEQUENCE { SEQUENCE, SEQUENCE, BIT STRING }. This rejects
    # accidental PKCS#12/private-key artifacts without importing certificates.
    def item(offset):
        assert offset + 2 <= len(data)
        tag, size = data[offset], data[offset+1]; start = offset + 2
        if size & 128:
            width = size & 127; assert 1 <= width <= 4 and start + width <= len(data)
            size = int.from_bytes(data[start:start+width], "big"); start += width
        assert start + size <= len(data)
        return tag, start, start + size
    tag, start, end = item(0); assert tag == 48 and end == len(data)
    tags = []
    while start < end:
        tag, _, start = item(start); tags.append(tag)
    assert tags == [48, 48, 3]
    return data

def verify(root):
    paths = list(root.glob("*.trx")); assert len(paths) == 1
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    tree = ET.parse(paths[0]); results = tree.findall(".//t:UnitTestResult", ns)
    assert len(results) == 5 and {result.attrib["testName"] for result in results} == EXPECTED
    assert all(result.attrib["outcome"] == "Passed" for result in results)
    counters = tree.find(".//t:Counters", ns)
    assert counters is not None and all(int(counters.attrib[key]) == value for key, value in {"total": 5, "executed": 5, "passed": 5, "failed": 0, "notExecuted": 0}.items())
    files = list((root / "evidence").glob("*.json")); assert {path.stem for path in files} == set(FACTS)
    nonces, issuers = set(), set()
    for name in FACTS:
        path = root / "evidence" / (name + ".json"); assert path.stat().st_size <= 512 * 1024
        row = json.loads(path.read_text(encoding="utf-8")); assert set(row) == KEYS and row["schemaVersion"] == 1 and row["fact"] == name
        assert row["fixtureNonce"] not in nonces; nonces.add(row["fixtureNonce"])
        issuer = der_certificate(row["issuerDer"]); assert issuer not in issuers; issuers.add(issuer)
        server = der_certificate(row["serverDer"]); der_certificate(row["rootDer"])
        assert row["serverHelloVersion"] == 0x0303
        decisions = row["decisions"]; assert decisions and all(set(decision) == {"Errors", "Trusted", "LeafSha256"} for decision in decisions)
        assert all(decision["LeafSha256"] == hashlib.sha256(server).hexdigest() for decision in decisions)
        if name == FACTS[0]:
            assert row["clientDer"] is None and row["verdict"] == "EnvironmentError" and row["authenticated"] is False
            assert certificate_message(row["serverCertificateMessage"]) == [server]
            assert len(decisions) == 1 and decisions[0]["Trusted"] is False and decisions[0]["Errors"] == 4
            control = row["controls"]; assert set(control) == {"nativeControlBuilt", "nativeControlAutomaticFetches", "productionAutomaticFetches", "endpointControlResponses"}
            assert control["nativeControlBuilt"] is True and control["nativeControlAutomaticFetches"] > 0 and control["productionAutomaticFetches"] == 0 and control["endpointControlResponses"] == 3
        else:
            assert row["controls"] == {"candidate": {FACTS[1]: "supplied", FACTS[2]: "omitted", FACTS[3]: "mismatched"}[name]}
            client = der_certificate(row["clientDer"])
            sent = certificate_message(row["clientCertificateMessage"])
            assert all(decision["Trusted"] is True for decision in decisions)
            if name == FACTS[1]:
                assert row["verdict"] == "Pass" and row["authenticated"] is True and row["protocol"] == "Tls12"
                assert sent and sent[0] == client and issuer in sent
            else:
                assert row["verdict"] == "EnvironmentError" and row["authenticated"] is False and issuer not in sent
                assert not sent or sent[0] == client
    print("TLS_HOSTED_ACCEPTANCE: four exact transport facts + final guard passed; zero skips; public DER/wire/AIA controls verified. TLS1.2-only wire observation; no global residue/private-key claim.")

if __name__ == "__main__":
    verify(Path(sys.argv[1]))
