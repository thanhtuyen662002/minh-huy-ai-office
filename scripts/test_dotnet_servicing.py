import importlib.util
from pathlib import Path
import subprocess
import sys
import unittest

path = Path(__file__).with_name("dotnet-servicing.py")
spec = importlib.util.spec_from_file_location("dotnet_servicing", path)
servicing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(servicing)

CORE = "Microsoft.NETCore.App 10.0.13 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"
ASP = "Microsoft.AspNetCore.App 10.0.13 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]"


class ServicingTests(unittest.TestCase):
    def test_current_and_newer_patch_in_same_sdk_feature_band(self):
        for version in ["10.0.402\n", "10.0.403\r\n", "10.0.499"]:
            with self.subTest(version=version):
                servicing.require_sdk(version)

    def test_old_wrong_band_preview_extra_or_oversized_sdk_refuses(self):
        for version in ["10.0.401", "10.0.113", "10.0.500", "11.0.402", "10.0.402-preview.1",
                        "10.0.402\nPRIVATE_CANARY", " 10.0.402", "10.0.0402", "10.0.402" + " " * 129, ""]:
            with self.subTest(version=version):
                with self.assertRaisesRegex(ValueError, servicing.INVALID):
                    servicing.require_sdk(version)

    def test_actual_linux_and_windows_public_runtime_shapes(self):
        servicing.require_frameworks(CORE + "\n" + ASP + "\n", aspnet=True)
        servicing.require_frameworks(CORE.replace("/usr/share/dotnet/shared/Microsoft.NETCore.App", r"C:\dotnet\shared\Microsoft.NETCore.App"))

    def test_newer_stable_patch_and_unrelated_known_framework_supported(self):
        servicing.require_frameworks(CORE.replace("10.0.13", "10.0.14") + "\n" + ASP.replace("10.0.13", "10.0.14"), True)
        servicing.require_frameworks(CORE + "\nMicrosoft.WindowsDesktop.App 10.0.13 [C:\\dotnet\\shared]")

    def test_missing_old_mixed_duplicate_preview_invalid_and_oversized_framework_refuses(self):
        variants = ["", ASP, CORE.replace("10.0.13", "10.0.12"), CORE + "\n" + CORE.replace("10.0.13", "10.0.12"),
                    CORE + "\n" + CORE, CORE.replace("10.0.13", "10.0.13-preview"),
                    CORE.replace("10.0.13", "11.0.13"), CORE + "\nPRIVATE_CANARY", CORE + "\n" * 17,
                    "Microsoft.NETCore.App 10.0.13 [" + "x" * 8192 + "]"]
        for text in variants:
            with self.subTest(index=variants.index(text)):
                with self.assertRaisesRegex(ValueError, servicing.INVALID):
                    servicing.require_frameworks(text)

    def test_both_required_aspnet_and_core_must_be_serviced(self):
        for text in [CORE, CORE + "\n" + ASP.replace("10.0.13", "10.0.12"), ASP, CORE + "\n" + ASP.replace("10.0.13", "9.0.21")]:
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    servicing.require_frameworks(text, True)

    def test_noncanonical_numeric_segments_refuse_in_host_and_container_modes(self):
        for version in ["010.0.13", "10.00.13", "10.0.013", "010.00.013", "10.0.+13", "10.0.-13"]:
            for host in [False, True]:
                with self.subTest(version=version, host=host):
                    with self.assertRaisesRegex(ValueError, servicing.INVALID):
                        servicing.require_frameworks(CORE.replace("10.0.13", version) + "\n" + ASP, True, host)

    def test_cli_reports_only_verified_versions_without_inventory_paths(self):
        result = subprocess.run([sys.executable, str(path), "--aspnet", "--host"],
                                input=CORE + "\n" + ASP, text=True, capture_output=True)
        self.assertEqual(0, result.returncode)
        self.assertIn("Microsoft.NETCore.App 10.0.13", result.stdout)
        self.assertIn("Microsoft.AspNetCore.App 10.0.13", result.stdout)
        self.assertNotIn("/usr/share", result.stdout)
        self.assertEqual("", result.stderr)

    def test_host_can_retain_inactive_old_installs_but_requires_serviced_compatible_selection(self):
        servicing.require_frameworks(CORE.replace("10.0.13", "10.0.12") + "\n" + CORE + "\n" + ASP, True, host=True)
        for text in [CORE.replace("10.0.13", "10.0.12"), CORE + "\n" + ASP.replace("10.0.13", "10.0.12"), CORE + "\n" + CORE + "\n" + ASP]:
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    servicing.require_frameworks(text, True, host=True)

    def test_cli_never_releases_rejected_private_text(self):
        for options in [["--sdk"], [], ["--aspnet"]]:
            result = subprocess.run([sys.executable, str(path), *options], input="PRIVATE_REJECTED_CANARY", text=True, capture_output=True)
            self.assertEqual(1, result.returncode)
            self.assertNotIn("PRIVATE_REJECTED_CANARY", result.stdout + result.stderr)
            self.assertEqual(servicing.INVALID + "\n", result.stderr)

    def test_cli_operates_on_actual_current_public_metadata(self):
        for options, text in [(["--sdk"], "10.0.402\n"), ([], CORE), (["--aspnet"], CORE + "\n" + ASP)]:
            result = subprocess.run([sys.executable, str(path), *options], input=text, text=True, capture_output=True)
            self.assertEqual(0, result.returncode)
            self.assertEqual("", result.stderr)
            self.assertTrue(result.stdout.startswith("PASS required October .NET"))


if __name__ == "__main__":
    unittest.main()
