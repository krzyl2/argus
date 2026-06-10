var builder = Host.CreateApplicationBuilder(args);
// TODO(wave3): register background workers here (HaListenerWorker, MqttPublisherWorker)
var host = builder.Build();
host.Run();
