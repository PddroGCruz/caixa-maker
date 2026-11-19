from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys

SEMVER_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
DEFAULT_ROOT = Path(__file__).resolve().parent.parent


def validate_semver(version: str) -> tuple[int, int, int]:
    """Valida e converte uma string MAJOR.MINOR.PATCH em tupla numérica."""
    match = SEMVER_RE.match(version.strip())
    if not match:
        raise ValueError(
            f"Versão SemVer inválida: '{version}'. Use estritamente MAJOR.MINOR.PATCH (ex.: 2.0.1)."
        )
    return int(match.group(1)), int(match.group(2)), int(match.group(3))


def bump_semver(version: str, part: str) -> str:
    """Calcula a próxima versão semântica para incrementos patch, minor ou major."""
    major, minor, patch = validate_semver(version)
    if part == "patch":
        return f"{major}.{minor}.{patch + 1}"
    if part == "minor":
        return f"{major}.{minor + 1}.0"
    if part == "major":
        return f"{major + 1}.0.0"
    raise ValueError(f"Tipo de incremento inválido: '{part}'. Use patch, minor ou major.")


def read_root_version(repo_root: Path = DEFAULT_ROOT) -> str:
    """Lê a versão centralizada no arquivo VERSION na raiz do repositório."""
    version_path = repo_root / "VERSION"
    if not version_path.exists():
        raise FileNotFoundError(f"Arquivo central de versão não encontrado: {version_path}")
    raw = version_path.read_text(encoding="utf-8").strip()
    validate_semver(raw)
    return raw


def write_root_version(version: str, repo_root: Path = DEFAULT_ROOT) -> None:
    """Atualiza o arquivo VERSION após validar formato SemVer."""
    validate_semver(version)
    (repo_root / "VERSION").write_text(f"{version.strip()}\n", encoding="utf-8")


def _update_directory_build_props(path: Path, expected: str, write: bool) -> list[str]:
    if not path.exists():
        return [f"{path}: arquivo Directory.Build.props não encontrado em native-winui/."]

    text = path.read_text(encoding="utf-8")
    expected_four_part = f"{expected}.0"
    tag_expectations: list[tuple[str, str, bool]] = [
        ("Version", expected, True),
        ("AssemblyVersion", expected_four_part, True),
        ("FileVersion", expected_four_part, True),
        ("InformationalVersion", expected, False),
    ]

    errors: list[str] = []
    updated = text

    for tag, target_val, required in tag_expectations:
        pattern = re.compile(rf"(<{tag}>)([^<]*)(</{tag}>)")
        match = pattern.search(updated)
        if not match:
            if required:
                errors.append(f"{path}: tag <{tag}> não encontrada em Directory.Build.props.")
            continue

        found_val = match.group(2).strip()
        if found_val != target_val:
            if write:
                updated = pattern.sub(rf"\g<1>{target_val}\g<3>", updated, count=1)
            else:
                errors.append(
                    f"{path}: <{tag}>='{found_val}' diverge do esperado ('{target_val}')."
                )

    if write and updated != text:
        path.write_text(updated, encoding="utf-8")
    return errors


def _update_regex_group(
    path: Path,
    pattern: re.Pattern[str],
    target_val: str,
    label: str,
    write: bool,
    required: bool = True,
) -> list[str]:
    if not path.exists():
        return [f"{path}: arquivo obrigatório não encontrado."] if required else []

    text = path.read_text(encoding="utf-8")
    match = pattern.search(text)
    if not match:
        return [f"{path}: padrão '{label}' não encontrado."]

    found_val = match.group(2).strip()
    if found_val == target_val:
        return []

    if write:
        updated = pattern.sub(rf"\g<1>{target_val}\g<3>", text, count=1)
        path.write_text(updated, encoding="utf-8")
        return []

    return [f"{path}: {label}='{found_val}' diverge do esperado ('{target_val}')."]


def _resolve_existing_or_primary(candidates: list[Path]) -> Path:
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]


def _sync_or_check_native_manifests(repo_root: Path, expected: str, write: bool) -> list[str]:
    expected_four_part = f"{expected}.0"
    native_dir = repo_root / "native-winui"
    errors: list[str] = []

    props_path = native_dir / "Directory.Build.props"
    errors.extend(_update_directory_build_props(props_path, expected, write=write))

    app_version_path = _resolve_existing_or_primary([
        native_dir / "src" / "CaixaMaker.Core" / "Storage" / "AppVersion.cs",
        native_dir / "src" / "CaixaMaker.Core" / "AppVersion.cs",
    ])
    errors.extend(
        _update_regex_group(
            app_version_path,
            re.compile(r'(typeof\(AppVersion\)\.Assembly\.GetName\(\)\.Version\?\.ToString\(\)\s*\?\?\s*")([^"]+)(")'),
            expected_four_part,
            "AppVersion.AssemblyVersion fallback",
            write,
        )
    )

    manifest_path = native_dir / "src" / "CaixaMaker.App" / "app.manifest"
    errors.extend(
        _update_regex_group(
            manifest_path,
            re.compile(r'(<assemblyIdentity\s+version=")([^"]+)("\s+name="CaixaMaker\.App\.app")'),
            expected_four_part,
            "assemblyIdentity/@version",
            write,
        )
    )

    nsis_path = _resolve_existing_or_primary([
        native_dir / "installer" / "nsis" / "CaixaMaker.nsi",
        repo_root / "installer" / "CaixaMaker.nsi",
    ])
    errors.extend(
        _update_regex_group(
            nsis_path,
            re.compile(r'(!define\s+VERSION\s+")([^"]+)(")'),
            expected,
            "VERSION",
            write,
        )
    )
    errors.extend(
        _update_regex_group(
            nsis_path,
            re.compile(r'(!define\s+BINARY_VERSION\s+")([^"]+)(")'),
            expected_four_part,
            "BINARY_VERSION",
            write,
        )
    )

    wxs_path = _resolve_existing_or_primary([
        native_dir / "installer" / "wix" / "Product.wxs",
        repo_root / "installer" / "Product.wxs",
    ])
    errors.extend(
        _update_regex_group(
            wxs_path,
            re.compile(r'(<\?define\s+Version\s*=\s*")([^"]+)("\s*\?>)'),
            expected,
            "Product.wxs Version",
            write,
        )
    )

    return errors


def sync_all_manifests(repo_root: Path = DEFAULT_ROOT) -> str:
    """Sincroniza todos os manifestos nativos do projeto a partir de VERSION."""
    version = read_root_version(repo_root)
    _sync_or_check_native_manifests(repo_root, version, write=True)
    return version


def check_consistency(repo_root: Path = DEFAULT_ROOT) -> list[str]:
    """Audita consistência de versão em todos os manifestos nativos e no CHANGELOG.md."""
    errors: list[str] = []
    version = read_root_version(repo_root)

    errors.extend(_sync_or_check_native_manifests(repo_root, version, write=False))

    changelog = repo_root / "CHANGELOG.md"
    if not changelog.exists():
        errors.append(f"{changelog}: arquivo CHANGELOG.md ausente.")
    else:
        cl_text = changelog.read_text(encoding="utf-8")
        if f"## [{version}]" not in cl_text:
            errors.append(
                f"{changelog}: seção '## [{version}]' não encontrada para a versão atual."
            )

    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Gerenciador centralizado de versionamento semântico do Caixa Maker (arquitetura nativa WinUI 3)."
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--current", action="store_true", help="Exibe a versão atual em VERSION.")
    group.add_argument("--sync", action="store_true", help="Sincroniza todos os manifestos nativos com VERSION.")
    group.add_argument("--check", action="store_true", help="Valida consistência entre VERSION, manifestos nativos e CHANGELOG.")
    group.add_argument("--bump", choices=("patch", "minor", "major"), help="Incrementa VERSION e sincroniza manifestos.")
    args = parser.parse_args(argv)

    if args.current:
        print(read_root_version())
        return 0

    if args.bump:
        old_ver = read_root_version()
        new_ver = bump_semver(old_ver, args.bump)
        write_root_version(new_ver)
        sync_all_manifests()
        print(f"Versao atualizada: {old_ver} -> {new_ver}")
        print("Lembrete: registre a entrega em CHANGELOG.md antes de distribuir.")
        return 0

    if args.sync:
        ver = sync_all_manifests()
        print(f"Manifestos sincronizados com VERSION={ver}")
        return 0

    if args.check:
        ver = read_root_version()
        errors = check_consistency()
        if errors:
            print(f"ERRO: inconsistencias detectadas para VERSION={ver}:", file=sys.stderr)
            for err in errors:
                print(f"  - {err}", file=sys.stderr)
            return 1
        print(f"OK: todos os manifestos, builds e CHANGELOG estao sincronizados na versao {ver}.")
        return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
