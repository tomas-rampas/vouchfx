"""Publication-input regressions use neutral synthetic files, never private bodies."""
from __future__ import annotations

import importlib.util
import json
import stat
import subprocess
import sys
import uuid
from pathlib import Path
from types import ModuleType, SimpleNamespace

import pytest

REPO_ROOT = Path(__file__).resolve().parents[3]


def _load(relative):
    name = f"_publication_test_{uuid.uuid4().hex}"
    spec = importlib.util.spec_from_file_location(name, REPO_ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def guard():
    return _load("scripts/site_hooks/_publication_inputs.py")


@pytest.fixture
def source_repo(tmp_path):
    inputs = [
        "README.md", "CHANGELOG.md", "GOVERNANCE.md", "docs/guide.md",
        "docs/assets/example.bin", "site/index.html", "overrides/404.html",
    ]
    for relative in inputs:
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("Neutral synthetic fixture.\n", encoding="utf-8")
    _manifest(tmp_path, inputs)
    return tmp_path, inputs


def _manifest(root, inputs):
    (root / "site-publication-inputs.json").write_text(
        json.dumps({"version": 1, "inputs": inputs}), encoding="utf-8",
    )


def _file(uri, source_dir, *, generated_by=None, inclusion=1, physical=None):
    return SimpleNamespace(
        src_uri=uri, src_dir=str(source_dir) if source_dir else None,
        generated_by=generated_by, inclusion=SimpleNamespace(value=inclusion),
        abs_src_path=str(physical) if physical else None,
        is_documentation_page=lambda: uri.endswith(".md"),
    )


def test_reviewed_pages_assets_and_metadata_pass(guard, source_repo):
    root, inputs = source_repo
    assert guard.validate_source_inputs(root) == frozenset(inputs)


@pytest.mark.parametrize("relative", [
    "docs/neutral-note.md", "docs/assets/neutral-note.txt", "docs/.neutral-note.txt",
    "site/neutral-note.txt", "overrides/neutral-note.html",
])
@pytest.mark.parametrize("tracked", [False, True])
def test_new_inputs_refuse_regardless_of_tracking(guard, source_repo, relative, tracked):
    root, _ = source_repo
    path = root / relative
    path.write_text("Body must never appear in a diagnostic.\n", encoding="utf-8")
    if tracked:
        subprocess.run(["git", "init", "--quiet", str(root)], check=True, capture_output=True)
        subprocess.run(["git", "-C", str(root), "add", relative], check=True, capture_output=True)
    with pytest.raises(guard.PublicationInputError, match="unregistered") as failure:
        guard.validate_source_inputs(root)
    assert relative in str(failure.value)
    assert "Body must never" not in str(failure.value)


def test_deliberately_registered_new_input_passes(guard, source_repo):
    root, inputs = source_repo
    relative = "docs/assets/approved.txt"
    (root / relative).write_text("Approved fixture.\n", encoding="utf-8")
    _manifest(root, inputs + [relative])
    assert relative in guard.validate_source_inputs(root)


@pytest.mark.parametrize("raw", [
    "", "{", "[]", "{}", '{"version":1,"inputs":[]}',
    '{"version":true,"inputs":["docs/guide.md"]}',
    '{"version":1,"version":1,"inputs":["docs/guide.md"]}',
    '{"version":1,"inputs":["docs/guide.md"],"unknown":1}',
])
def test_empty_or_malformed_manifest_refuses(guard, source_repo, raw):
    root, _ = source_repo
    (root / "site-publication-inputs.json").write_text(raw, encoding="utf-8")
    with pytest.raises(guard.PublicationInputError, match="manifest"):
        guard.validate_source_inputs(root)


@pytest.mark.parametrize("entry", [
    "docs/../outside.md", "/docs/guide.md", "docs//guide.md", "docs/./guide.md",
    "docs\\guide.md", "C:/guide.md", "docs/*.md", "docs/guide.md ",
    "docs/guide.md.", "docs/guide\n.md", "docs/cafe\u0301.md", 42, None,
    "src/neutral.txt", ".", "docs", "docs/03_MVP_Project_Plan.md", "docs/reviews/neutral.md",
    "docs/03_mvp_project_plan.md", "docs/Reviews/neutral.md",
])
def test_noncanonical_or_disallowed_manifest_entry_refuses(guard, source_repo, entry):
    root, inputs = source_repo
    _manifest(root, inputs + [entry])
    with pytest.raises(guard.PublicationInputError, match="manifest"):
        guard.validate_source_inputs(root)


@pytest.mark.parametrize("alias", ["docs/guide.md", "docs/Guide.md"])
def test_duplicate_and_case_alias_manifest_entries_refuse(guard, source_repo, alias):
    root, inputs = source_repo
    _manifest(root, inputs + [alias])
    with pytest.raises(guard.PublicationInputError, match="duplicate/case-alias"):
        guard.validate_source_inputs(root)


def test_missing_manifest_refuses(guard, source_repo):
    root, _ = source_repo
    (root / "site-publication-inputs.json").unlink()
    with pytest.raises(guard.PublicationInputError, match="unreadable input: site-publication"):
        guard.validate_source_inputs(root)


def test_missing_registered_file_refuses(guard, source_repo):
    root, _ = source_repo
    (root / "docs/guide.md").unlink()
    with pytest.raises(guard.PublicationInputError, match="manifest input missing"):
        guard.validate_source_inputs(root)


def test_manifest_without_public_markdown_refuses(guard, source_repo):
    root, inputs = source_repo
    _manifest(root, [p for p in inputs if p != "docs/guide.md"])
    with pytest.raises(guard.PublicationInputError, match="public Markdown are required"):
        guard.validate_source_inputs(root)


def test_known_private_exclusions_remain_outside_manifest(guard, source_repo):
    root, inputs = source_repo
    for relative in ["docs/03_MVP_Project_Plan.md", "docs/reviews/neutral.md"]:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("Inert exclusion fixture.\n", encoding="utf-8")
    assert guard.validate_source_inputs(root) == frozenset(inputs)


def test_symlink_input_is_rejected_without_following_it(guard, source_repo, tmp_path):
    root, _ = source_repo
    target = tmp_path.parent / f"neutral-{uuid.uuid4().hex}.txt"
    target.write_text("Inert target.\n", encoding="utf-8")
    try:
        (root / "docs/neutral-link.txt").symlink_to(target)
    except OSError as exc:
        pytest.skip(f"Symlink creation unavailable: {type(exc).__name__}")
    with pytest.raises(guard.PublicationInputError, match="symlink/reparse input"):
        guard.validate_source_inputs(root)


def test_windows_reparse_attribute_is_rejected(guard):
    path = SimpleNamespace(lstat=lambda: SimpleNamespace(
        st_mode=stat.S_IFREG, st_file_attributes=0x400,
    ))
    with pytest.raises(guard.PublicationInputError, match="symlink/reparse input"):
        guard._check_regular(path, "docs/neutral.txt")


def test_final_collection_accepts_registered_pages_and_known_generated_blog(guard, source_repo):
    root, _ = source_repo
    inputs = guard.validate_source_inputs(root)
    files = [
        _file("guide.md", root / "docs"),
        _file("assets/example.bin", root / "docs"),
        _file("blog/archive/2026.md", root / "blog-temp", generated_by="material/blog"),
        _file("404.html", root / "overrides", physical=root / "overrides/404.html"),
    ]
    guard.validate_mkdocs_files(files, root, inputs, [str(root / "overrides")])


@pytest.mark.parametrize("uri", ["neutral.md", "assets/neutral.txt"])
def test_final_collection_rejects_late_unregistered_docs_input(guard, source_repo, uri):
    root, _ = source_repo
    files = [_file("guide.md", root / "docs"), _file(uri, root / "docs", generated_by="material/blog")]
    with pytest.raises(guard.PublicationInputError, match="unregistered collection input"):
        guard.validate_mkdocs_files(files, root, guard.validate_source_inputs(root), [])


def test_final_collection_cannot_be_empty(guard, source_repo):
    root, _ = source_repo
    with pytest.raises(guard.PublicationInputError, match="no registered public Markdown"):
        guard.validate_mkdocs_files([], root, guard.validate_source_inputs(root), [])


def test_final_collection_cannot_exclude_every_registered_page(guard, source_repo):
    root, _ = source_repo
    files = [_file("guide.md", root / "docs", inclusion=-3)]
    with pytest.raises(guard.PublicationInputError, match="no registered public Markdown"):
        guard.validate_mkdocs_files(files, root, guard.validate_source_inputs(root), [])


@pytest.mark.parametrize("uri", ["reviews/neutral.md", "Reviews/neutral.md", "03_mvp_project_plan.md"])
def test_private_collection_entry_must_be_fully_excluded(guard, source_repo, uri):
    root, _ = source_repo
    files = [_file("guide.md", root / "docs"), _file(uri, root / "docs", inclusion=-2)]
    with pytest.raises(guard.PublicationInputError, match="private source included"):
        guard.validate_mkdocs_files(files, root, guard.validate_source_inputs(root), [])
    files[-1].inclusion.value = -3
    guard.validate_mkdocs_files(files, root, guard.validate_source_inputs(root), [])


def test_unrecognised_generated_or_external_file_refuses(guard, source_repo):
    root, _ = source_repo
    files = [_file("guide.md", root / "docs"), _file("neutral.md", None, generated_by="other")]
    with pytest.raises(guard.PublicationInputError, match="unsupported collection input"):
        guard.validate_mkdocs_files(files, root, guard.validate_source_inputs(root), [])


def test_direct_deployment_gate_rechecks_source_manifest(source_repo, monkeypatch):
    gate = _load("scripts/check_site.py")
    root, _ = source_repo
    monkeypatch.setattr(gate, "REPO_ROOT", root)
    gate.check_publication_inputs(root)
    (root / "site/neutral-note.txt").write_text("Inert fixture.\n", encoding="utf-8")
    with pytest.raises(gate.CheckFailed, match="unregistered asset/metadata: site/neutral-note.txt"):
        gate.check_publication_inputs(root)
    assert gate.CHECKS[0] is gate.check_publication_inputs


def test_current_reviewed_manifest_matches_public_source_names(guard):
    inputs = guard.validate_source_inputs(REPO_ROOT)
    assert len(inputs) >= 40
    assert "docs/01_Technical_Architecture_and_Engineering_Blueprint.md" in inputs
    assert "docs/roadmap.md" in inputs
    assert "site/og-image.png" in inputs


@pytest.fixture
def early_hook(monkeypatch):
    # The site-tools-only CI lane does not install MkDocs. Stub only its two
    # import-time symbols there; docs-environment tests use real MkDocs below.
    try:
        import mkdocs.exceptions
    except ImportError:
        exceptions = ModuleType("mkdocs.exceptions")
        exceptions.PluginError = type("PluginError", (RuntimeError,), {})
        plugins = ModuleType("mkdocs.plugins")
        plugins.event_priority = lambda priority: lambda event: event
        monkeypatch.setitem(sys.modules, "mkdocs.exceptions", exceptions)
        monkeypatch.setitem(sys.modules, "mkdocs.plugins", plugins)
    return _load("scripts/site_hooks/publication_inputs.py")


@pytest.mark.parametrize("directive", [
    '--8<-- "plan/neutral.md"',
    "-8<- 'docs/reviews/neutral.md'",
    '--8<-- "docs/03_MVP_Project_Plan.md:1:3"',
    '--8<-- "HUMAN_TODO.md:section"',
    '--8<-- "docs/assets/example.bin"',
    '--8<-- "docs/unregistered.md"',
    '--8<-- "../neutral.md"',
    '--8<-- "https://example.invalid/neutral.md"',
    '--8<--\nplan/neutral.md\n--8<--',
    '--8<--\n"plan/neutral.md"\n--8<--',
])
@pytest.mark.parametrize("source", ["docs/guide.md", "README.md"])
def test_early_hook_rejects_direct_and_nested_targets_without_reading_them(
    early_hook, source_repo, monkeypatch, directive, source,
):
    root, _ = source_repo
    # The root snippet is reachable from a reviewed public page.
    (root / "docs/guide.md").write_text('--8<-- "README.md"', encoding="utf-8")
    (root / source).write_text(directive, encoding="utf-8")
    private = root / "docs/reviews/neutral.md"
    private.parent.mkdir(parents=True)
    private.write_text("PRIVATE BODY MUST NOT BE READ", encoding="utf-8")
    monkeypatch.setattr(early_hook, "REPO_ROOT", root)
    original = Path.read_text
    reads = []

    def admitted_read(path, *args, **kwargs):
        reads.append(path.relative_to(root).as_posix())
        assert path != private, "private directive target was opened"
        return original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "read_text", admitted_read)
    with pytest.raises(early_hook.PluginError, match="not in the allowlist"):
        early_hook.on_config({"docs_dir": str(root / "docs")})
    assert set(reads).issubset({"docs/guide.md", "README.md", "CHANGELOG.md", "GOVERNANCE.md"})


@pytest.mark.parametrize("event", ["on_config", "on_files"])
def test_early_hook_admits_all_names_before_scanning_any_body(
    early_hook, source_repo, monkeypatch, event,
):
    root, _ = source_repo
    (root / "docs/unregistered.md").write_text("UNREVIEWED BODY", encoding="utf-8")
    monkeypatch.setattr(early_hook, "REPO_ROOT", root)

    def no_body_reads(*args, **kwargs):
        pytest.fail("body read before source names were admitted")

    monkeypatch.setattr(Path, "read_text", no_body_reads)
    config = {"docs_dir": str(root / "docs")}
    with pytest.raises(early_hook.PluginError, match="unregistered Markdown"):
        if event == "on_config":
            early_hook.on_config(config)
        else:
            early_hook.on_files([], config=config)


def test_early_hook_accepts_recursive_approved_and_escaped_directives(
    early_hook, source_repo, monkeypatch,
):
    root, _ = source_repo
    (root / "docs/guide.md").write_text('--8<-- "README.md:1:8"', encoding="utf-8")
    (root / "README.md").write_text(
        "--8<--\n;plan/ignored.md\nCHANGELOG.md\n--8<--\n"
        ';--8<-- "plan/escaped.md"', encoding="utf-8",
    )
    (root / "CHANGELOG.md").write_text("-8<- 'GOVERNANCE.md'", encoding="utf-8")
    monkeypatch.setattr(early_hook, "REPO_ROOT", root)
    config = {"docs_dir": str(root / "docs"), "theme": SimpleNamespace(dirs=[])}
    assert early_hook.on_config(config) is config
    files = [_file("guide.md", root / "docs")]
    assert early_hook.on_files(files, config=config) is files


def test_postbuild_snippet_gate_rechecks_nested_snippets(source_repo, monkeypatch):
    gate = _load("scripts/check_site.py")
    root, _ = source_repo
    monkeypatch.setattr(gate, "REPO_ROOT", root)
    gate.check_snippet_allowlist(root)
    (root / "README.md").write_text('--8<-- "plan/neutral.md"', encoding="utf-8")
    with pytest.raises(gate.CheckFailed, match="README.md:1:.*not in the allowlist"):
        gate.check_snippet_allowlist(root)


@pytest.mark.parametrize("target", [None, "docs/reviews/neutral.md", "plan/neutral.md"])
def test_real_mkdocs_build_admits_snippets_before_markdown_render(
    source_repo, monkeypatch, target,
):
    # Run this integration guard with requirements-docs.txt installed. The
    # site-tools-only lane still exercises both real hook entry points above.
    pytest.importorskip("mkdocs")
    pytest.importorskip("pymdownx.snippets")
    import shutil
    import markdown
    from mkdocs.commands.build import build
    from mkdocs.config import load_config
    from mkdocs.exceptions import Abort, PluginError

    root, _ = source_repo
    hooks = root / "scripts/site_hooks"
    hooks.mkdir(parents=True)
    for name in ["publication_inputs.py", "_publication_inputs.py"]:
        shutil.copyfile(REPO_ROOT / "scripts/site_hooks" / name, hooks / name)
    (root / "docs/guide.md").write_text('--8<-- "README.md"', encoding="utf-8")
    (root / "README.md").write_text('--8<-- "CHANGELOG.md"', encoding="utf-8")
    if target:
        path = root / target
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("PRIVATE BODY MUST NOT BE READ", encoding="utf-8")
        (root / "CHANGELOG.md").write_text(f'--8<-- "{target}"', encoding="utf-8")
    else:
        (root / "CHANGELOG.md").write_text("APPROVED NESTED CONTENT", encoding="utf-8")
    (root / "mkdocs.yml").write_text(
        "site_name: Neutral fixture\nsite_dir: _build\nplugins: []\n"
        "hooks: [scripts/site_hooks/publication_inputs.py]\n"
        "markdown_extensions:\n  - pymdownx.snippets:\n      base_path: ['" + root.as_posix() + "']\n",
        encoding="utf-8",
    )
    config = load_config(config_file=str(root / "mkdocs.yml"), strict=True)
    if target:
        def no_render(*args, **kwargs):
            pytest.fail("Markdown rendered before snippet admission")

        original = Path.read_text
        def no_private_read(path, *args, **kwargs):
            assert path != root / target, "private snippet target was read"
            return original(path, *args, **kwargs)

        monkeypatch.setattr(markdown.Markdown, "convert", no_render)
        monkeypatch.setattr(Path, "read_text", no_private_read)
        with pytest.raises(Abort, match="BuildError") as failure:
            build(config)
        assert isinstance(failure.value.__context__, PluginError)
        assert "not in the allowlist" in str(failure.value.__context__)
        assert not (root / "_build/guide/index.html").exists()
    else:
        build(config)
        assert "APPROVED NESTED CONTENT" in (root / "_build/guide/index.html").read_text(encoding="utf-8")
