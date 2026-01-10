$ErrorActionPreference = "Stop"

$runner = "tester/runner/GamTester/GamTester.csproj"
$dataset = "tester/datasets/sample-dataset.json"
$scenario = "tester/scenarios/switch-a-b.json"
$results = "tester/results/runs.csv"

dotnet run --project $runner -- --dataset $dataset --scenario $scenario --condition LM --repeat 3 --results $results
dotnet run --project $runner -- --dataset $dataset --scenario $scenario --condition BL --repeat 3 --results $results

Write-Host "Benchmark complete. Results: $results"
